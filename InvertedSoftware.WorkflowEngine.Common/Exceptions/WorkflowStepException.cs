// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Common.Exceptions;

public class WorkflowStepException : Exception
{
    public WorkflowStepException() { }
    public WorkflowStepException(string s) : base(s) { }
    public WorkflowStepException(string s, Exception innerException) : base(s, innerException) { }
}
