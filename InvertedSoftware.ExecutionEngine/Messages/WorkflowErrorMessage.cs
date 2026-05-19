// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Messages;

/// <summary>Error summary written to the Error queue when a step fails.</summary>
public class WorkflowErrorMessage
{
    public string JobName { get; set; } = string.Empty;
    public string StepName { get; set; } = string.Empty;
    public string ExceptionMessage { get; set; } = string.Empty;
}
