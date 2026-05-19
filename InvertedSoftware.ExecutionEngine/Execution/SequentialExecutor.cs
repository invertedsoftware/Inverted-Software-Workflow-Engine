// Copyright (c) Inverted Software. All rights reserved.

using InvertedSoftware.WorkflowEngine.DataObjects;
using InvertedSoftware.WorkflowEngine.Diagnostics;
using InvertedSoftware.WorkflowEngine.Messages;
using Microsoft.Extensions.Logging;

namespace InvertedSoftware.WorkflowEngine.Execution;

/// <summary>
/// Executor that runs each step strictly in order. Default on single-core hosts
/// and when <c>EngineOptions.UsePipelinedOnMulticore = false</c>.
/// </summary>
internal sealed class SequentialExecutor : IExecutor
{
    private readonly StepExecutor _stepExecutor;
    private readonly IJobReporter _reporter;
    private readonly ILogger<SequentialExecutor> _logger;

    public ProcessorJob ProcessorJob { get; set; } = new();

    public SequentialExecutor(WorkflowEngineHost host, IJobReporter reporter)
    {
        _stepExecutor = new StepExecutor(host, reporter);
        _reporter = reporter;
        _logger = host.CreateLogger<SequentialExecutor>();
    }

    public async Task RunFrameworkJobAsync(
        IWorkflowMessage workflowMessage,
        int retryJobTimes,
        bool isCheckDepends,
        CancellationToken cancellationToken)
    {
        var currentJob = ProcessorJob.DeepCopy();

        foreach (var workflowStep in currentJob.WorkFlowSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                workflowStep.RunStatus = FrameworkStepRunStatus.Waiting;
                if (workflowStep.RunMode == StepExecutionMode.Synchronous)
                {
                    await _stepExecutor.RunFrameworkStepAsync(
                        workflowMessage, retryStepTimes: 0, workflowStep, currentJob, isCheckDepends, cancellationToken)
                        .ConfigureAwait(false);
                }
                else // FireAndForget
                {
                    // Wrap to surface unobserved infrastructure exceptions in logs. The
                    // StepExecutor catches user-step exceptions and routes them through
                    // _reporter, but failures that fall outside its try-block (e.g. an
                    // idempotency store outage, a step-factory throw) would otherwise
                    // disappear into the void.
                    var capturedStep = workflowStep;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _stepExecutor.RunFrameworkStepAsync(
                                workflowMessage, retryStepTimes: 0, capturedStep, currentJob, isCheckDepends, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { /* expected */ }
                        catch (Exception e)
                        {
                            Log.FireAndForgetStepFaulted(_logger, e, currentJob.JobName, capturedStep.StepName);
                        }
                    }, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                workflowStep.ExitMessage = e.Message;
                switch (workflowStep.OnError)
                {
                    case OnFrameworkStepError.RetryJob:
                        if (workflowStep.WaitBetweenRetriesMilliseconds > 0)
                            await Task.Delay(workflowStep.WaitBetweenRetriesMilliseconds, cancellationToken).ConfigureAwait(false);
                        retryJobTimes++;
                        await _reporter.ReportJobErrorAsync(e, workflowStep, workflowMessage, currentJob, cancellationToken).ConfigureAwait(false);
                        if (retryJobTimes <= workflowStep.RetryTimes)
                            await RunFrameworkJobAsync(workflowMessage, retryJobTimes, isCheckDepends, cancellationToken).ConfigureAwait(false);
                        return;
                    case OnFrameworkStepError.Skip:
                        await _reporter.ReportJobErrorAsync(e, workflowStep, workflowMessage, currentJob, cancellationToken).ConfigureAwait(false);
                        break;
                    case OnFrameworkStepError.Exit:
                        await _reporter.ReportJobErrorAsync(e, workflowStep, workflowMessage, currentJob, cancellationToken).ConfigureAwait(false);
                        return;
                }
            }
        }

        if (currentJob.NotifyComplete &&
            currentJob.WorkFlowSteps.All(s => s.RunStatus == FrameworkStepRunStatus.Complete))
        {
            await _reporter.ReportJobCompleteAsync(workflowMessage, currentJob, cancellationToken).ConfigureAwait(false);
        }
    }
}
