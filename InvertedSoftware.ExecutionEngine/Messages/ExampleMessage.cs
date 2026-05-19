// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Messages;

/// <summary>Sample message used by the WPF and Console demo apps.</summary>
public class ExampleMessage : IWorkflowMessage
{
    public int JobID { get; set; }
    public string CopyFilesFrom { get; set; } = string.Empty;
    public string CopyFilesTo { get; set; } = string.Empty;
}
