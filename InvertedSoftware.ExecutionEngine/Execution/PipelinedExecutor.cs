// Copyright (c) Inverted Software. All rights reserved.

using System.Collections.Concurrent;
using System.Threading.Tasks.Dataflow;
using InvertedSoftware.WorkflowEngine.DataObjects;
using InvertedSoftware.WorkflowEngine.Messages;

namespace InvertedSoftware.WorkflowEngine.Execution;

/// <summary>
/// TPL Dataflow–based executor. Each step in the workflow becomes a
/// <see cref="TransformBlock{TInput,TOutput}"/> linked to the next; messages flow
/// through the pipeline with <c>MaxDegreeOfParallelism = FrameworkMaxThreads</c>.
/// Multiple jobs can be in flight at once; each job's completion is correlated
/// to its submitting caller via a per-job <see cref="TaskCompletionSource{TResult}"/>.
/// </summary>
internal sealed class PipelinedExecutor : IExecutor
{
    private readonly WorkflowEngineHost _host;
    private readonly IJobReporter _reporter;
    private readonly StepExecutor _stepExecutor;
    private readonly List<TransformBlock<PipelineInfo, PipelineInfo>> _workerBlocks = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<PipelineInfo>> _pending = new();

    private ProcessorJob _processorJob = new();
    public ProcessorJob ProcessorJob
    {
        get => _processorJob;
        set
        {
            _processorJob = value;
            LoadPipeline();
        }
    }

    public PipelinedExecutor(WorkflowEngineHost host, IJobReporter reporter)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        _stepExecutor = new StepExecutor(host, reporter);
    }

    private void LoadPipeline()
    {
        _workerBlocks.Clear();
        for (var i = 0; i < _processorJob.WorkFlowSteps.Count; i++)
        {
            var block = new TransformBlock<PipelineInfo, PipelineInfo>(
                async pi => await RunParalleledPipelinedStepAsync(pi).ConfigureAwait(false),
                new ExecutionDataflowBlockOptions
                {
                    MaxDegreeOfParallelism = _host.Options.FrameworkMaxThreads,
                });

            if (_workerBlocks.Count > 0)
                _workerBlocks[^1].LinkTo(block, new DataflowLinkOptions { PropagateCompletion = true });
            _workerBlocks.Add(block);
        }

        // Terminal action: dispatch each completed PipelineInfo back to its caller via
        // the per-job TaskCompletionSource. This fixes the v1 bug where any waiting
        // RunFrameworkJobAsync call would receive any job's result.
        if (_workerBlocks.Count > 0)
        {
            var terminalBlock = new ActionBlock<PipelineInfo>(info =>
            {
                if (_pending.TryRemove(info.JobInstanceId, out var tcs))
                    tcs.TrySetResult(info);
            });
            _workerBlocks[^1].LinkTo(terminalBlock, new DataflowLinkOptions { PropagateCompletion = true });
        }
    }

    private async Task<PipelineInfo> RunParalleledPipelinedStepAsync(PipelineInfo info)
    {
        if (!info.IsInProcess) return info;

        var workflowStep = info.CurrentJob.WorkFlowSteps[info.CurrentStepNumber];

        try
        {
            info.CancellationToken.ThrowIfCancellationRequested();
            workflowStep.RunStatus = FrameworkStepRunStatus.Waiting;

            if (workflowStep.RunMode == StepExecutionMode.Synchronous)
            {
                await _stepExecutor.RunFrameworkStepAsync(
                    info.WorkflowMessage, info.RetryStepTimes, workflowStep, info.CurrentJob, info.IsCheckDepends, info.CancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                _ = Task.Run(() => _stepExecutor.RunFrameworkStepAsync(
                    info.WorkflowMessage, info.RetryStepTimes, workflowStep, info.CurrentJob, info.IsCheckDepends, info.CancellationToken),
                    info.CancellationToken);
            }

            info.CurrentStepNumber++;
            return info;
        }
        catch (OperationCanceledException)
        {
            info.IsInProcess = false;
            return info;
        }
        catch (Exception e)
        {
            workflowStep.ExitMessage = e.Message;

            switch (workflowStep.OnError)
            {
                case OnFrameworkStepError.RetryJob:
                    if (workflowStep.WaitBetweenRetriesMilliseconds > 0)
                        await Task.Delay(workflowStep.WaitBetweenRetriesMilliseconds, info.CancellationToken).ConfigureAwait(false);
                    info.RetryJobTimes++;
                    await _reporter.ReportJobErrorAsync(e, workflowStep, info.WorkflowMessage, info.CurrentJob, info.CancellationToken).ConfigureAwait(false);
                    if (info.RetryJobTimes <= workflowStep.RetryTimes)
                        await RunFrameworkJobAsync(info.WorkflowMessage, info.RetryJobTimes, info.IsCheckDepends, info.CancellationToken).ConfigureAwait(false);
                    info.IsInProcess = false;
                    return info;
                case OnFrameworkStepError.Skip:
                    await _reporter.ReportJobErrorAsync(e, workflowStep, info.WorkflowMessage, info.CurrentJob, info.CancellationToken).ConfigureAwait(false);
                    info.CurrentStepNumber++;
                    return info;
                case OnFrameworkStepError.Exit:
                default:
                    await _reporter.ReportJobErrorAsync(e, workflowStep, info.WorkflowMessage, info.CurrentJob, info.CancellationToken).ConfigureAwait(false);
                    info.IsInProcess = false;
                    return info;
            }
        }
    }

    public async Task RunFrameworkJobAsync(
        IWorkflowMessage workflowMessage,
        int retryJobTimes,
        bool isCheckDepends,
        CancellationToken cancellationToken)
    {
        if (_workerBlocks.Count == 0) return;

        var currentJob = _processorJob.DeepCopy();
        var jobInstanceId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<PipelineInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[jobInstanceId] = tcs;

        // Wire cancellation: if the per-job token fires, abandon the wait so callers don't hang.
        using var registration = cancellationToken.Register(static state =>
        {
            ((TaskCompletionSource<PipelineInfo>)state!).TrySetCanceled();
        }, tcs);

        var info = new PipelineInfo
        {
            JobInstanceId = jobInstanceId,
            CurrentJob = currentJob,
            IsCheckDepends = isCheckDepends,
            RetryJobTimes = retryJobTimes,
            RetryStepTimes = 0,
            WorkflowMessage = workflowMessage,
            IsInProcess = true,
            CurrentStepNumber = 0,
            CancellationToken = cancellationToken,
        };

        try
        {
            await _workerBlocks[0].SendAsync(info, cancellationToken).ConfigureAwait(false);

            // Wait specifically for THIS job's result, not "any job's result".
            PipelineInfo result;
            try { result = await tcs.Task.ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            if (result.IsInProcess &&
                currentJob.NotifyComplete &&
                currentJob.WorkFlowSteps.All(s => s.RunStatus == FrameworkStepRunStatus.Complete))
            {
                await _reporter.ReportJobCompleteAsync(workflowMessage, currentJob, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _pending.TryRemove(jobInstanceId, out _);
        }
    }
}

/// <summary>State threaded through the TPL Dataflow pipeline.</summary>
internal struct PipelineInfo
{
    public Guid JobInstanceId { get; set; }
    public IWorkflowMessage WorkflowMessage { get; set; }
    public int RetryStepTimes { get; set; }
    public int RetryJobTimes { get; set; }
    public ProcessorJob CurrentJob { get; set; }
    public bool IsCheckDepends { get; set; }
    public bool IsInProcess { get; set; }
    public int CurrentStepNumber { get; set; }
    public CancellationToken CancellationToken { get; set; }
}
