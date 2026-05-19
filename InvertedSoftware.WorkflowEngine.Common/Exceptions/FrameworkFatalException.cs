// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Common.Exceptions;

public class FrameworkFatalException : Exception
{
    public FrameworkFatalException() { }
    public FrameworkFatalException(string s) : base(s) { }
    public FrameworkFatalException(string s, Exception innerException) : base(s, innerException) { }
}
