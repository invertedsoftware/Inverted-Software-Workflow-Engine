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

        _stopConsumingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
    /// them. When <c>false</c>, in-flight jobs see a cancelled token and nack-requeue.
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
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
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

        try
        {
            await foreach (var received in _host.QueueProvider
                .ConsumeAsync(_processorJob.JobName, consumeOptions, stopConsuming)
                .ConfigureAwait(false))
            {
                // Bound the pool wait to `shutdown` (not stopConsuming) so a soft-stop
                // doesn't drop a message we've already received from the broker; the
                // soft-stop contract is "finish what you have". Hard-stop still bails.
                await _pool.WaitAsync(shutdown).ConfigureAwait(false);
                Interlocked.Increment(ref JobsRunning);
                WorkflowTelemetry.JobsInFlight.Add(1,
                    new KeyValuePair<string, object?>("job", _processorJob.JobName));

                _ = Task.Run(() => RunFrameworkJobAsync(received, shutdown), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stopConsuming.IsCancellationRequested || shutdown.IsCancellationRequested)
        {
            // Graceful: StopFrameworkAsync was called (soft or hard).
        }
    }

    private async Task RunFrameworkJobAsync(IReceivedMessage received, CancellationToken outerToken)
    {
        var jobName = _processorJob.JobName;

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
        => QueueOperationsHandler.HandleCompleteAsync(_host, currentJob.JobName, workflowMessage, cancellationToken);

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
        return QueueOperationsHandler.HandleErrorAsync(_host, currentJob.JobName, workflowMessage, error, cancellationToken);
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
            await _host.QueueProvider.PublishAsync(
                new LogicalQueue(_processorJob.JobName, LogicalQueueKind.Poison),
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
