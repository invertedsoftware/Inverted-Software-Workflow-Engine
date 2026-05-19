// Copyright (c) Inverted Software. All rights reserved.

using InvertedSoftware.WorkflowEngine.DataObjects;
using InvertedSoftware.WorkflowEngine.Messages;

namespace InvertedSoftware.WorkflowEngine.Execution;

internal interface IExecutor
{
    /// <summary>The job template; cloned per invocation by the executor.</summary>
    ProcessorJob ProcessorJob { get; set; }

    /// <summary>Execute the job's steps for a single message.</summary>
    Task RunFrameworkJobAsync(
        IWorkflowMessage workflowMessage,
        int retryJobTimes,
        bool isCheckDepends,
        CancellationToken cancellationToken);
}
