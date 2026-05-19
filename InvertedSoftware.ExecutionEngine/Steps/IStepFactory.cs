// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Steps;

/// <summary>
/// Resolves an <see cref="IStep"/> instance by the type name declared in
/// <c>Workflow.xml</c> (the <c>InvokeClass</c> attribute).
/// </summary>
public interface IStepFactory
{
    /// <summary>
    /// Resolve the step. Throws
    /// <see cref="InvertedSoftware.WorkflowEngine.Common.Exceptions.WorkflowStepException"/>
    /// when no matching type is registered or discoverable.
    /// </summary>
    IStep GetStep(string invokeClassName);
}
