// Copyright (c) Inverted Software. All rights reserved.

using System.Diagnostics;
using InvertedSoftware.WorkflowEngine.Common.Security;
using InvertedSoftware.WorkflowEngine.DataObjects;
using InvertedSoftware.WorkflowEngine.Diagnostics;
using InvertedSoftware.WorkflowEngine.Idempotency;
using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.Queue;
using InvertedSoftware.WorkflowEngine.Steps;
using Microsoft.Extensions.Logging;

namespace InvertedSoftware.WorkflowEngine.Execution;

/// <summary>
/// Invokes a single <see cref="IStep"/>. Handles dependency waits, retry policy,
/// idempotency claiming, telemetry, and (Windows-only) impersonation.
///
/// <para><b>Idempotency model:</b> the claim is acquired ONCE at the outer entry
/// (<see cref="RunFrameworkStepAsync"/>). Per-step retries via <c>OnError=RetryStep</c>
/// re-execute the step body without re-claiming, so distributed implementations that
/// reject duplicate claims (e.g. Redis <c>SET NX EX</c>) don't accidentally short-circuit
/// the retry. The claim is marked COMPLETED only on successful execution — failure
/// (retries exhausted, propagated exception) RELEASES the claim so future job-level
/// retries or consumer-crash redeliveries can re-attempt the step. Cancellation leaves
/// the claim in place to let the store's TTL govern when redelivery may retry.</para>
/// </summary>
internal sealed class StepExecutor
{
    private readonly WorkflowEngineHost _host;
    private readonly IJobReporter _reporter;
    private readonly ILogger<StepExecutor> _logger;

    public StepExecutor(WorkflowEngineHost host, IJobReporter reporter)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        _logger = host.CreateLogger<StepExecutor>();
    }

    /// <summary>
    /// Outer entry. Acquires the idempotency claim, delegates step execution +
    /// retries to <see cref="RunStepBodyAsync"/>, then marks the claim completed
    /// on success or releases it on failure. Rethrows after release so the
    /// executor's <c>OnError</c> logic still sees the failure for Synchronous steps.
    /// </summary>
    internal async Task RunFrameworkStepAsync(
        IWorkflowMessage workflowMessage,
        int retryStepTimes,
        ProcessorStep workflowStep,
        ProcessorJob currentJob,
        bool isCheckDepends,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var claim = new IdempotencyClaim(currentJob.JobName, workflowStep.StepName, workflowMessage.JobID);

        // Wrap the claim call so a store-side failure (e.g. Redis timeout) is logged as
        // an idempotency-infrastructure issue rather than being silently misreported as
        // a step failure. We still propagate so the message nack-requeues and a later
        // delivery — when the store is healthy again — can re-attempt the claim.
        bool shouldRun;
        try
        {
            shouldRun = await _host.IdempotencyStore.TryClaimAsync(claim, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Log.IdempotencyClaimFailed(_logger, e, currentJob.JobName, workflowMessage.JobID, workflowStep.StepName);
            throw;
        }

        if (!shouldRun)
        {
            Log.StepSkippedIdempotent(_logger, currentJob.JobName, workflowMessage.JobID, workflowStep.StepName);
            workflowStep.RunStatus = FrameworkStepRunStatus.Complete;
            workflowStep.ExitMessage = "Skipped: idempotency store reports already completed.";
            WorkflowTelemetry.StepDuration.Record(0,
                new KeyValuePair<string, object?>("job", currentJob.JobName),
                new KeyValuePair<string, object?>("step", workflowStep.StepName),
                new KeyValuePair<string, object?>("outcome", "skipped"));
            return;
        }

        try
        {
            await RunStepBodyAsync(workflowMessage, retryStepTimes, workflowStep, currentJob, isCheckDepends, cancellationToken)
                .ConfigureAwait(false);

            // Success — the step has run to completion. Mark it so future redeliveries
            // skip it.
            await _host.IdempotencyStore.MarkCompletedAsync(claim, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation isn't a terminal disposition — the claim is intentionally
            // left in place so the implementation's TTL governs when a future
            // redelivery may retry. Rethrow without marking or releasing.
            throw;
        }
        catch
        {
            // Failure — release the claim so OnError=RetryJob and consumer-crash
            // redeliveries can re-attempt the step. We do NOT want a failed step's
            // claim to look "completed" to the next attempt; that would silently turn
            // failures into successes.
            //
            // Release uses CancellationToken.None: the failure has already happened
            // and we want the release to land even if the outer token is already
            // cancelled (e.g. shutdown raced with step failure). If the store itself
            // throws, log it and let the ORIGINAL step exception propagate — the
            // job's OnError handler is designed for step failures, not store failures.
            try
            {
                await _host.IdempotencyStore.ReleaseAsync(claim, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception releaseFailure)
            {
                Log.IdempotencyReleaseFailed(_logger, releaseFailure, currentJob.JobName, workflowMessage.JobID, workflowStep.StepName);
            }
            throw;
        }
    }

    /// <summary>
    /// Inner: runs the step, handles dependency wait, telemetry, and the retry
    /// recursion. Does NOT touch the idempotency store — the outer call owns that.
    /// </summary>
    private async Task RunStepBodyAsync(
        IWorkflowMessage workflowMessage,
        int retryStepTimes,
        ProcessorStep workflowStep,
        ProcessorJob currentJob,
        bool isCheckDepends,
        CancellationToken cancellationToken)
    {
        var needsImpersonation =
            !string.IsNullOrEmpty(workflowStep.RunAsDomain) &&
            !string.IsNullOrEmpty(workflowStep.RunAsUser) &&
            !string.IsNullOrEmpty(workflowStep.RunAsPassword) &&
            workflowStep.RunMode == StepExecutionMode.Synchronous;

        if (needsImpersonation && !OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                $"Step '{workflowStep.StepName}' specifies RunAsUser, which requires Windows.");
        }

        using var stepActivity = WorkflowTelemetry.ActivitySource.StartActivity(
            Telemetry.Activities.Step, ActivityKind.Internal);
        stepActivity?.SetTag(Telemetry.Tags.JobName, currentJob.JobName);
        stepActivity?.SetTag(Telemetry.Tags.JobId, workflowMessage.JobID);
        stepActivity?.SetTag(Telemetry.Tags.StepName, workflowStep.StepName);
        if (retryStepTimes > 0)
            stepActivity?.SetTag("workflow.retry_attempt", retryStepTimes);

        using IStep step = _host.StepFactory.GetStep(workflowStep.InvokeClass);
        var stopwatch = Stopwatch.StartNew();
        var outcome = "complete";

        try
        {
            workflowStep.RunStatus = FrameworkStepRunStatus.Loaded;
            if (isCheckDepends)
                await WaitForDependentsAsync(workflowStep, currentJob, cancellationToken).ConfigureAwait(false);

            workflowStep.StartDate = DateTime.UtcNow;
            Log.StepStarting(_logger, currentJob.JobName, workflowMessage.JobID, workflowStep.StepName);

            if (needsImpersonation && OperatingSystem.IsWindows())
            {
                var imp = new Impersonation
                {
                    ImpersonationLogonType = ImpersonationLogonType.LOGON32_LOGON_NEW_CREDENTIALS,
                };
                imp.RunAs(workflowStep.RunAsUser, workflowStep.RunAsDomain, workflowStep.RunAsPassword,
                    () => step.RunStep(workflowMessage, cancellationToken));
            }
            else
            {
                step.RunStep(workflowMessage, cancellationToken);
            }

            workflowStep.EndDate = DateTime.UtcNow;
            workflowStep.RunStatus = FrameworkStepRunStatus.Complete;
            workflowStep.ExitMessage = "Complete";

            stopwatch.Stop();
            Log.StepCompleted(_logger, currentJob.JobName, workflowMessage.JobID, workflowStep.StepName, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            workflowStep.RunStatus = FrameworkStepRunStatus.CompleteWithErrors;
            workflowStep.ExitMessage = "Cancelled (deadline exceeded or shutdown).";
            stepActivity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            throw;
        }
        catch (Exception e)
        {
            outcome = "error";
            workflowStep.RunStatus = FrameworkStepRunStatus.CompleteWithErrors;
            workflowStep.ExitMessage = e.Message;
            stepActivity?.SetStatus(ActivityStatusCode.Error, e.Message);

            var inner = e.InnerException;
            while (inner is not null)
            {
                workflowStep.ExitMessage += $"|{inner.Message}";
                inner = inner.InnerException;
            }

            Log.StepFailed(_logger, e, currentJob.JobName, workflowMessage.JobID, workflowStep.StepName,
                retryStepTimes + 1, workflowStep.RetryTimes);

            switch (workflowStep.OnError)
            {
                case OnFrameworkStepError.RetryStep:
                    if (retryStepTimes >= workflowStep.RetryTimes)
                    {
                        // Retries exhausted. Propagate to the outer entry so it can mark
                        // the claim completed and let the job executor's OnError logic
                        // (Skip / Exit / RetryJob) deal with it.
                        throw;
                    }
                    if (workflowStep.WaitBetweenRetriesMilliseconds > 0)
                        await Task.Delay(workflowStep.WaitBetweenRetriesMilliseconds, cancellationToken).ConfigureAwait(false);
                    // Retry the body, not the outer wrapper — keeps the claim held.
                    await RunStepBodyAsync(workflowMessage, retryStepTimes + 1, workflowStep, currentJob, isCheckDepends, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    // Inline (Synchronous) steps propagate the error up to the job executor;
                    // fire-and-forget steps don't have anybody to throw to, so report directly.
                    if (workflowStep.RunMode == StepExecutionMode.Synchronous)
                        throw;
                    await _reporter.ReportJobErrorAsync(e, workflowStep, workflowMessage, currentJob, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            stopwatch.Stop();
            stepActivity?.SetTag(Telemetry.Tags.StepOutcome, outcome);
            WorkflowTelemetry.StepDuration.Record(stopwatch.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>("job", currentJob.JobName),
                new KeyValuePair<string, object?>("step", workflowStep.StepName),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    private static async Task WaitForDependentsAsync(ProcessorStep workflowStep, ProcessorJob currentJob, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(workflowStep.DependsOn) && string.IsNullOrEmpty(workflowStep.DependsOnGroup))
            return;

        var dependsOnSteps = workflowStep.DependsOn.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var dependsOnGroups = workflowStep.DependsOnGroup.Split(',', StringSplitOptions.RemoveEmptyEntries);

        var waited = 0;
        const int pollMs = 100;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var anyPending = currentJob.WorkFlowSteps.Any(s =>
                s.RunStatus != FrameworkStepRunStatus.Complete &&
                (dependsOnSteps.Contains(s.StepName) || dependsOnGroups.Contains(s.Group)));

            if (!anyPending) return;
            if (waited > workflowStep.WaitForDependsOnMilliseconds) return;

            await Task.Delay(pollMs, cancellationToken).ConfigureAwait(false);
            waited += pollMs;
            workflowStep.RunStatusTime += pollMs;
        }
    }
}
