// Copyright (c) Inverted Software. All rights reserved.

using InvertedSoftware.WorkflowEngine.Messages;

namespace InvertedSoftware.WorkflowEngine.Steps;

/// <summary>
/// Contract for a workflow step. The runtime invokes <see cref="RunStep"/> once
/// per job; long-running implementations MUST honour <paramref name="cancellationToken"/>
/// to support the per-job <c>MaxRunTimeMilliseconds</c> deadline (v1 used
/// <c>Thread.Abort()</c>, which is no longer available in .NET 10).
/// </summary>
public interface IStep : IDisposable
{
    /// <summary>
    /// Executes this step.
    /// </summary>
    /// <param name="message">The message that triggered this job.</param>
    /// <param name="cancellationToken">
    /// Cancellation token that fires when the job exceeds its <c>MaxRunTimeMilliseconds</c>
    /// budget or the engine is stopped.
    /// </param>
    void RunStep(IWorkflowMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Optional helper base class that calls <see cref="CancellationToken.ThrowIfCancellationRequested"/>
/// before delegating to <see cref="RunStepCore"/>.
/// </summary>
public abstract class StepBase : IStep
{
    public void RunStep(IWorkflowMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunStepCore(message, cancellationToken);
    }

    protected abstract void RunStepCore(IWorkflowMessage message, CancellationToken cancellationToken);

    public virtual void Dispose() => GC.SuppressFinalize(this);
}
