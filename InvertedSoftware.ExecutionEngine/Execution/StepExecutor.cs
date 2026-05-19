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

    internal async Task RunFrameworkStepAsync(
        IWorkflowMessage workflowMessage,
        int retryStepTimes,
        ProcessorStep workflowStep,
        ProcessorJob currentJob,
        bool isCheckDepends,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

        // Idempotency check — skip already-completed steps under at-least-once redelivery.
        var claim = new IdempotencyClaim(currentJob.JobName, workflowStep.StepName, workflowMessage.JobID);
        var shouldRun = await _host.IdempotencyStore.TryClaimAsync(claim, cancellationToken).ConfigureAwait(false);
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

        using var stepActivity = WorkflowTelemetry.ActivitySource.StartActivity(
            Telemetry.Activities.Step, ActivityKind.Internal);
        stepActivity?.SetTag(Telemetry.Tags.JobName, currentJob.JobName);
        stepActivity?.SetTag(Telemetry.Tags.JobId, workflowMessage.JobID);
        stepActivity?.SetTag(Telemetry.Tags.StepName, workflowStep.StepName);

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

            await _host.IdempotencyStore.MarkCompletedAsync(claim, cancellationToken).ConfigureAwait(false);

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
                    if (workflowStep.WaitBetweenRetriesMilliseconds > 0)
                        await Task.Delay(workflowStep.WaitBetweenRetriesMilliseconds, cancellationToken).ConfigureAwait(false);
                    retryStepTimes++;
                    if (retryStepTimes <= workflowStep.RetryTimes)
                        await RunFrameworkStepAsync(workflowMessage, retryStepTimes, workflowStep, currentJob, isCheckDepends, cancellationToken).ConfigureAwait(false);
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
