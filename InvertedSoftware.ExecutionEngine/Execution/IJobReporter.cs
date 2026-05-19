// Copyright (c) Inverted Software. All rights reserved.

using InvertedSoftware.WorkflowEngine.DataObjects;
using InvertedSoftware.WorkflowEngine.Messages;

namespace InvertedSoftware.WorkflowEngine.Execution;

/// <summary>
/// Callback the executors use to push error / completion notifications to the
/// outer queue infrastructure. Implemented by <see cref="Processor"/>.
/// </summary>
internal interface IJobReporter
{
    /// <summary>Publish an error summary + the original message body to the Error/Poison queues.</summary>
    Task ReportJobErrorAsync(
        Exception exception,
        ProcessorStep workflowStep,
        IWorkflowMessage workflowMessage,
        ProcessorJob currentJob,
        CancellationToken cancellationToken);

    /// <summary>Publish the message to the Completed queue (when <c>NotifyComplete</c> is set and all steps succeeded).</summary>
    Task ReportJobCompleteAsync(
        IWorkflowMessage workflowMessage,
        ProcessorJob currentJob,
        CancellationToken cancellationToken);
}
