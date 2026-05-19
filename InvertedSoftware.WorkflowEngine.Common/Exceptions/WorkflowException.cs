// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Common.Exceptions;

public class WorkflowException : Exception
{
    public WorkflowException() { }
    public WorkflowException(string s) : base(s) { }
    public WorkflowException(string s, Exception innerException) : base(s, innerException) { }
}
