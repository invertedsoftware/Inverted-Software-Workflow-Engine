// Copyright (c) Inverted Software. All rights reserved.

using System.Diagnostics;
using InvertedSoftware.WorkflowEngine.Common;
using InvertedSoftware.WorkflowEngine.Config;
using InvertedSoftware.WorkflowEngine.DataObjects;
using InvertedSoftware.WorkflowEngine.Diagnostics;
using InvertedSoftware.WorkflowEngine.Execution;
using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.Queue;
using Microsoft.Extensions.Logging;

namespace InvertedSoftware.WorkflowEngine;

/// <summary>
/// Consumes messages from the main queue and dispatches them to an
/// <see cref="IExecutor"/>. Implements <see cref="IJobReporter"/> so executors
/// can publish error / completion notifications back through the same provider.
/// </summary>
public sealed class Processor : IJobReporter, IDisposable
{
    private readonly WorkflowEngineHost _host;
    private readonly ILogger<Processor> _logger;
    private readonly SemaphoreSlim _pool;

    private ProcessorJob _processorJob = new();
    private IExecutor? _executor;
    // The tier the *currently-executing job* was consumed from. Stored as an
    // AsyncLocal<int> so that multiple in-flight jobs each carry their own value
    // along their async-flow — without it, the outer consume loop's tier
    // re-selection would race with in-flight jobs' IJobReporter callbacks and
    // route Error / Poison / Completed messages to the wrong tier.
    private readonly AsyncLocal<int> _ambientTier = new();
    // Two-stage cancellation:
    //   _stopConsumingCts fires first (StopFrameworkAsync, soft and hard) and unblocks
    //     the ConsumeAsync iterator so no new messages are picked up.
    //   _shutdownCts fires on hard-stop only; in-flight jobs see this and nack-requeue.
    // A pure soft-stop cancels _stopConsumingCts but lets running jobs ack normally.
    private CancellationTokenSource? _stopConsumingCts;
    private CancellationTokenSource? _shutdownCts;

    public Processor(WorkflowEngineHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _logger = host.CreateLogger<Processor>();
        _pool = new SemaphoreSlim(_host.Options.FrameworkMaxThreads, _host.Options.FrameworkMaxThreads);
    }

    /// <summary>Count of jobs currently executing.</summary>
    public int JobsRunning;

    /// <summary>True while <see cref="StartFrameworkAsync"/> has not yet returned.</summary>
    public bool FrameworkOn { get; private set; }

    /// <summary>
    /// Start consuming the named job's main queue and processing messages.
    /// Returns when the framework stops (after <see cref="StopFrameworkAsync"/> is invoked
    /// or the supplied <paramref name="cancellationToken"/> fires).
    ///
    /// <para><b>Cancellation semantics:</b> firing <paramref name="cancellationToken"/>
    /// is treated as a SOFT-stop signal — the consume loop stops accepting new
    /// messages, but in-flight jobs continue to natural completion. This matches
    /// the contract callers usually expect from a host-shutdown token (graceful drain,
    /// not abort). For an immediate, in-flight-cancelling hard stop, invoke
    /// <see cref="StopFrameworkAsync"/> with <c>isSoftExit: false</c>.</para>
    /// </summary>
    public async Task StartFrameworkAsync(string jobName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobName);

        _processorJob = new ProcessorJob { JobName = jobName };
        _host.Configuration.LoadFrameworkConfig(_processorJob);

        _executor = (Utils.PROCESSOR_COUNT > 1 && _host.Options.UsePipelinedOnMulticore)
            ? new PipelinedExecutor(_host, this)
            : new SequentialExecutor(_host, this);
        _executor.ProcessorJob = _processorJob;

        // External cancellation is a SOFT-stop: it cancels stopConsuming so the iterator
        // exits, but does NOT cancel _shutdownCts — in-flight jobs keep running to
        // completion. Hard-stop must go through StopFrameworkAsync(isSoftExit:false).
        _stopConsumingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _shutdownCts = new CancellationTokenSource();
        FrameworkOn = true;
        Log.FrameworkStarted(_logger, jobName);

        try
        {
            await RunFrameworkAsync().ConfigureAwait(false);
        }
        finally
        {
            FrameworkOn = false;
            Log.FrameworkStopped(_logger, jobName);
        }
    }

    /// <summary>
    /// Stop the consumer. When <paramref name="isSoftExit"/> is <c>true</c>, in-flight
    /// jobs continue running to natural completion and ack normally; this call awaits
    /// them, honouring <paramref name="cancellationToken"/> as a drain deadline. When
    /// <c>false</c>, in-flight jobs see a cancelled token and nack-requeue; the call
    /// returns immediately.
    ///
    /// <para>If the soft-drain is interrupted by <paramref name="cancellationToken"/>
    /// (e.g. the host's shutdown timeout fires), this method returns rather than
    /// throwing — the caller can decide to escalate to hard-stop. In-flight jobs that
    /// haven't finished by then continue running until they complete on their own;
    /// they are NOT cancelled by this method.</para>
    /// </summary>
    public async Task StopFrameworkAsync(bool isSoftExit, CancellationToken cancellationToken = default)
    {
        Log.FrameworkStopping(_logger, _processorJob.JobName, isSoftExit);
        _stopConsumingCts?.Cancel();

        if (!isSoftExit)
        {
            _shutdownCts?.Cancel();
            return;
        }

        while (Volatile.Read(ref JobsRunning) > 0)
        {
            try
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Drain deadline reached — return without throwing. The caller chose
                // soft-exit, so we don't escalate; they can call StopFrameworkAsync(false)
                // if they want to force in-flight cancellation.
                return;
            }
        }
    }

    /// <summary>Synchronous shim — prefer <see cref="StopFrameworkAsync"/>.</summary>
    public void StopFramework(bool isSoftExit) =>
        StopFrameworkAsync(isSoftExit).GetAwaiter().GetResult();

    /// <summary>Synchronous shim — prefer <see cref="StartFrameworkAsync"/>.</summary>
    public void StartFramework(string jobName) =>
        StartFrameworkAsync(jobName).GetAwaiter().GetResult();

    private async Task RunFrameworkAsync()
    {
        var consumeOptions = new ConsumeOptions
        {
            Prefetch = _host.Options.FrameworkMaxThreads,
            AutoAck = _processorJob.MessageQueueType == MessageQueueType.NonTransactional,
            AckTimeout = TimeSpan.FromMilliseconds(Math.Max(_processorJob.MaxRunTimeMilliseconds, 60_000)),
        };

        var stopConsuming = _stopConsumingCts!.Token;
        var shutdown = _shutdownCts!.Token;
        var rebalanceInterval = TimeSpan.FromSeconds(Math.Max(_host.Options.TierRebalanceIntervalSeconds, 5));

        // Outer loop: pick a tier, consume from it until the rebalance timer or an
        // error forces us to re-evaluate. Stops only when stopConsuming fires.
        // `selectedTier` is the *outer loop's* state — local because rebalance may
        // change it while in-flight jobs (which carry their own `_ambientTier.Value`)
        // continue to use whatever tier their message came from.
        while (!stopConsuming.IsCancellationRequested)
        {
            var selectedTier = await SelectBestTierAsync(shutdown).ConfigureAwait(false);
            if (TierCount > 1)
                Log.ConsumingFromTier(_logger, _processorJob.JobName, selectedTier);

            // The rebalance CTS fires after the interval, forcing the consume iterator
            // to exit and the outer loop to re-select. This is how a multi-tier
            // consumer notices when the primary recovers and switches back.
            using var rebalanceCts = TierCount > 1
                ? CancellationTokenSource.CreateLinkedTokenSource(stopConsuming)
                : null;
            rebalanceCts?.CancelAfter(rebalanceInterval);
            var consumeToken = rebalanceCts?.Token ?? stopConsuming;

            try
            {
                await foreach (var received in _host.QueueProvider
                    .ConsumeAsync(_processorJob.JobName, consumeOptions, selectedTier, consumeToken)
                    .ConfigureAwait(false))
                {
                    // Bound the pool wait to `shutdown` (not consumeToken) so a soft-stop
                    // or rebalance doesn't drop a message we've already received.
                    await _pool.WaitAsync(shutdown).ConfigureAwait(false);
                    Interlocked.Increment(ref JobsRunning);
                    WorkflowTelemetry.JobsInFlight.Add(1,
                        new KeyValuePair<string, object?>("job", _processorJob.JobName));

                    var capturedTier = selectedTier;
                    _ = Task.Run(() => RunFrameworkJobAsync(received, capturedTier, shutdown), CancellationToken.None);
                }
            }
            catch (OperationCanceledException) when (stopConsuming.IsCancellationRequested || shutdown.IsCancellationRequested)
            {
                return; // graceful stop
            }
            catch (OperationCanceledException)
            {
                // Rebalance timer fired; loop to re-select the best tier.
            }
            catch (QueueUnavailableException e)
            {
                Log.ConsumerTierUnavailable(_logger, e, _processorJob.JobName, selectedTier);
                try { await Task.Delay(TimeSpan.FromSeconds(2), stopConsuming).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// Picks the consumer tier per v1 semantics: iterate declared tiers in REVERSE
    /// order; pick the first that's reachable and has pending work. If no tier
    /// reports messages, fall back to tier 0 (primary) and wait for new arrivals.
    ///
    /// <para>For single-queue jobs (one or zero <c>&lt;Queue&gt;</c> entries) this
    /// is a no-op that returns 0 without consulting the broker.</para>
    /// </summary>
    private async Task<int> SelectBestTierAsync(CancellationToken cancellationToken)
    {
        var count = TierCount;
        if (count == 1) return 0;

        for (var tier = count - 1; tier >= 0; tier--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var health = await _host.QueueProvider
                    .CheckHealthAsync(_processorJob.JobName, tier, cancellationToken)
                    .ConfigureAwait(false);
                if (health.MainAvailable && (health.ApproximateMainDepth ?? 0) > 0)
                    return tier;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // honour shutdown; don't keep probing tiers
            }
            catch (QueueProviderException)
            {
                // Tier unreachable; try the next one.
            }
        }
        // No tier had messages; default to the primary so we sit on the queue
        // most likely to receive new work.
        return 0;
    }

    private int TierCount => Math.Max(1, _processorJob.ProcessorQueues.Count);

    private async Task RunFrameworkJobAsync(IReceivedMessage received, int tier, CancellationToken outerToken)
    {
        var jobName = _processorJob.JobName;
        // Stash the tier the consumer was on when this message was received, so
        // the IJobReporter implementation routes secondary publishes to the same
        // tier (Error+Poison+Completed colocate with where the work came from).
        // AsyncLocal so concurrent jobs don't clobber each other's value.
        _ambientTier.Value = tier;

        // Start a consumer-side Activity linked to the producer's traceparent.
        using var activity = StartConsumeActivity(received, jobName);

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        jobCts.CancelAfter(_processorJob.MaxRunTimeMilliseconds);

        var stopwatch = Stopwatch.StartNew();
        IWorkflowMessage? workflowMessage = null;
        var outcome = "complete";
        try
        {
            // Deserialize and type-check. A wrong-type body (schema drift, mismatched
            // MessageType header) is treated as a deserialization error, not a generic
            // failure — without this guard the InvalidCastException would silently ack
            // the message.
            var deserialized = received.DeserializeBody(_host.Serializer);
            if (deserialized is not IWorkflowMessage msg)
            {
                throw new MessageDeserializationException(
                    $"Body deserialized to '{deserialized?.GetType().FullName ?? "null"}' which does not implement IWorkflowMessage.");
            }
            workflowMessage = msg;

            // Detect rerun BEFORE tagging/logging so telemetry shows the absolute JobID
            // with an explicit is_rerun flag, rather than a confusing negative number.
            var isRerun = workflowMessage.JobID < 0;
            if (isRerun) workflowMessage.JobID = -workflowMessage.JobID;
            var isCheckDepends = !isRerun;

            activity?.SetTag(Telemetry.Tags.JobId, workflowMessage.JobID);
            if (isRerun) activity?.SetTag("workflow.is_rerun", true);
            Log.JobReceived(_logger, jobName, received.Headers.MessageId, workflowMessage.JobID);

            await _executor!.RunFrameworkJobAsync(workflowMessage, retryJobTimes: 0, isCheckDepends, jobCts.Token).ConfigureAwait(false);
            await received.AckAsync(outerToken).ConfigureAwait(false);

            stopwatch.Stop();
            Log.JobCompleted(_logger, jobName, workflowMessage.JobID, stopwatch.ElapsedMilliseconds);
        }
        catch (MessageDeserializationException e)
        {
            outcome = "deserialization_error";
            activity?.SetStatus(ActivityStatusCode.Error, e.Message);
            Log.JobDeserializationFailed(_logger, e, jobName, received.Headers.MessageType);
            WorkflowTelemetry.Errors.Add(1,
                new KeyValuePair<string, object?>("job", jobName),
                new KeyValuePair<string, object?>("kind", "deserialization"));
            await PublishPoisonAsync(received, e.Message, outerToken).ConfigureAwait(false);
            await received.AckAsync(outerToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (jobCts.IsCancellationRequested && !outerToken.IsCancellationRequested)
        {
            outcome = "timeout";
            stopwatch.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, "job timeout");
            WorkflowTelemetry.Errors.Add(1,
                new KeyValuePair<string, object?>("job", jobName),
                new KeyValuePair<string, object?>("kind", "timeout"));
            if (workflowMessage is not null)
            {
                Log.JobTimedOut(_logger, jobName, workflowMessage.JobID, stopwatch.ElapsedMilliseconds, _processorJob.MaxRunTimeMilliseconds);
                var timeoutStep = _processorJob.WorkFlowSteps.FirstOrDefault() ?? new ProcessorStep { StepName = "<unknown>" };
                await ReportJobErrorAsync(
                    new TimeoutException($"Job exceeded MaxRunTimeMilliseconds={_processorJob.MaxRunTimeMilliseconds}"),
                    timeoutStep, workflowMessage, _processorJob, outerToken).ConfigureAwait(false);
            }
            await received.AckAsync(outerToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (outerToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            // Engine shutdown: requeue so a later worker can try again.
            try { await received.NackAsync(requeue: true, CancellationToken.None).ConfigureAwait(false); }
            catch { /* best-effort on shutdown */ }
        }
        catch (Exception e)
        {
            outcome = "error";
            activity?.SetStatus(ActivityStatusCode.Error, e.Message);
            WorkflowTelemetry.Errors.Add(1,
                new KeyValuePair<string, object?>("job", jobName),
                new KeyValuePair<string, object?>("kind", "step_failure"));
            if (workflowMessage is not null)
            {
                var step = _processorJob.WorkFlowSteps.FirstOrDefault() ?? new ProcessorStep { StepName = "<unknown>" };
                Log.JobFailed(_logger, e, jobName, workflowMessage.JobID, step.StepName);
                await ReportJobErrorAsync(e, step, workflowMessage, _processorJob, outerToken).ConfigureAwait(false);
            }
            await received.AckAsync(outerToken).ConfigureAwait(false);
        }
        finally
        {
            stopwatch.Stop();
            _pool.Release();
            Interlocked.Decrement(ref JobsRunning);

            WorkflowTelemetry.JobsInFlight.Add(-1,
                new KeyValuePair<string, object?>("job", jobName));
            WorkflowTelemetry.JobsProcessed.Add(1,
                new KeyValuePair<string, object?>("job", jobName),
                new KeyValuePair<string, object?>("outcome", outcome));
            WorkflowTelemetry.JobDuration.Record(stopwatch.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>("job", jobName),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    private static Activity? StartConsumeActivity(IReceivedMessage received, string jobName)
    {
        // Pull parent context from the W3C traceparent header (set by the producer).
        ActivityContext parentContext = default;
        if (received.Headers.TryGetValue(Telemetry.TraceHeaders.TraceParent, out var traceparent)
            && ActivityContext.TryParse(traceparent, null, out var ctx))
        {
            parentContext = ctx;
        }

        var activity = WorkflowTelemetry.ActivitySource.StartActivity(
            Telemetry.Activities.Consume,
            ActivityKind.Consumer,
            parentContext);

        activity?.SetTag(Telemetry.Tags.JobName, jobName);
        activity?.SetTag(Telemetry.Tags.MessagingOperation, "receive");
        if (received.Headers.MessageId is { } messageId)
            activity?.SetTag(Telemetry.Tags.MessageId, messageId);

        return activity;
    }

    // ---- IJobReporter --------------------------------------------------------

    Task IJobReporter.ReportJobErrorAsync(Exception exception, ProcessorStep workflowStep, IWorkflowMessage workflowMessage, ProcessorJob currentJob, CancellationToken cancellationToken)
        => ReportJobErrorAsync(exception, workflowStep, workflowMessage, currentJob, cancellationToken);

    Task IJobReporter.ReportJobCompleteAsync(IWorkflowMessage workflowMessage, ProcessorJob currentJob, CancellationToken cancellationToken)
        => QueueOperationsHandler.HandleCompleteAsync(_host, currentJob.JobName, _ambientTier.Value, TierCount, workflowMessage, cancellationToken);

    private Task ReportJobErrorAsync(Exception exception, ProcessorStep workflowStep, IWorkflowMessage workflowMessage, ProcessorJob currentJob, CancellationToken cancellationToken)
    {
        var error = new WorkflowErrorMessage
        {
            JobName = currentJob.JobName,
            StepName = workflowStep.StepName,
            ExceptionMessage = exception.Message,
        };
        var inner = exception.InnerException;
        while (inner is not null)
        {
            error.ExceptionMessage += $"|{inner.Message}";
            inner = inner.InnerException;
        }
        return QueueOperationsHandler.HandleErrorAsync(_host, currentJob.JobName, _ambientTier.Value, TierCount, workflowMessage, error, cancellationToken);
    }

    private async Task PublishPoisonAsync(IReceivedMessage received, string reason, CancellationToken cancellationToken)
    {
        var headers = new MessageHeaders
        {
            ContentType = _host.Serializer.ContentType,
            MessageType = received.Headers.MessageType,
            CorrelationId = received.Headers.CorrelationId,
        };
        headers["x-wf-poison-reason"] = reason;

        try
        {
            // Poison goes to the SAME tier the message came from.
            await _host.QueueProvider.PublishAsync(
                new LogicalQueue(_processorJob.JobName, LogicalQueueKind.Poison, _ambientTier.Value),
                received.Body,
                headers,
                cancellationToken).ConfigureAwait(false);
        }
        catch (QueueProviderException e)
        {
            // Best-effort — the original is being ack'd regardless to prevent a poison loop,
            // but we log so the broker outage is visible.
            Log.SecondaryPublishFailed(_logger, e, _processorJob.JobName, "Poison");
            WorkflowTelemetry.Errors.Add(1,
                new KeyValuePair<string, object?>("job", _processorJob.JobName),
                new KeyValuePair<string, object?>("kind", "secondary_publish"));
        }
    }

    public void Dispose()
    {
        _stopConsumingCts?.Cancel();
        _shutdownCts?.Cancel();
        _stopConsumingCts?.Dispose();
        _shutdownCts?.Dispose();
        _pool.Dispose();
    }
}
