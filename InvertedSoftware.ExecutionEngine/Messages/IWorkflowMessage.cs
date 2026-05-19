// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Messages;

/// <summary>
/// Marker interface for messages enqueued onto a workflow queue. Any message
/// posted through <c>FrameworkManager.AddFrameworkJobAsync</c> must implement
/// this interface.
/// </summary>
public interface IWorkflowMessage
{
    /// <summary>The job ID. Negative values indicate a re-run (dependency checks skipped).</summary>
    int JobID { get; set; }
}
