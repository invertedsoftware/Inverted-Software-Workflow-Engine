// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.DataObjects;

public enum OnFrameworkStepError
{
    RetryJob,
    Skip,
    RetryStep,
    Exit,
}

/// <summary>
/// Execution mode for a step. <see cref="Synchronous"/> runs the step inline on the
/// job thread; <see cref="FireAndForget"/> spawns it on the thread pool and does not
/// wait. The legacy names <c>STA</c> / <c>MTA</c> are accepted by the Workflow.xml
/// parser but the runtime no longer has anything to do with COM apartments.
/// </summary>
public enum StepExecutionMode
{
    Synchronous,
    FireAndForget,
}

/// <summary>Legacy alias of <see cref="StepExecutionMode"/> kept for binary back-compat.</summary>
[Obsolete("Use StepExecutionMode. STA maps to Synchronous, MTA maps to FireAndForget.")]
public enum FrameworkStepRunMode
{
    STA = StepExecutionMode.Synchronous,
    MTA = StepExecutionMode.FireAndForget,
}

public enum FrameworkStepRunStatus
{
    Loaded,
    Waiting,
    Complete,
    CompleteWithErrors,
}

/// <summary>
/// A single framework step.
/// </summary>
public class ProcessorStep
{
    #region Config
    public string StepName { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string InvokeClass { get; set; } = string.Empty;
    public OnFrameworkStepError OnError { get; set; } = OnFrameworkStepError.Skip;
    public int RetryTimes { get; set; }
    public int WaitBetweenRetriesMilliseconds { get; set; }

    /// <summary>Synchronous (inline) or FireAndForget (background task).</summary>
    public StepExecutionMode RunMode { get; set; } = StepExecutionMode.Synchronous;

    public string DependsOn { get; set; } = string.Empty;
    public string DependsOnGroup { get; set; } = string.Empty;
    public int WaitForDependsOnMilliseconds { get; set; } = int.MaxValue;

    /// <summary>Used for impersonation (Windows-only). Only honoured in <see cref="StepExecutionMode.Synchronous"/>.</summary>
    public string RunAsDomain { get; set; } = string.Empty;
    /// <summary>Used for impersonation (Windows-only). Only honoured in <see cref="StepExecutionMode.Synchronous"/>.</summary>
    public string RunAsUser { get; set; } = string.Empty;
    /// <summary>Used for impersonation (Windows-only). Only honoured in <see cref="StepExecutionMode.Synchronous"/>.</summary>
    public string RunAsPassword { get; set; } = string.Empty;
    #endregion

    #region Runtime
    public FrameworkStepRunStatus RunStatus { get; set; } = FrameworkStepRunStatus.Loaded;
    public int RunStatusTime { get; set; }
    #endregion

    #region Log
    public int FrameworkJobStepID { get; set; } = -1;
    public int FrameworkJobID { get; set; } = -1;
    public DateTime? CreatedDate { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string ExitMessage { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
    #endregion

    public ProcessorStep DeepCopy() => new()
    {
        StepName = StepName,
        Group = Group,
        InvokeClass = InvokeClass,
        OnError = OnError,
        RetryTimes = RetryTimes,
        WaitBetweenRetriesMilliseconds = WaitBetweenRetriesMilliseconds,
        RunMode = RunMode,
        DependsOn = DependsOn,
        DependsOnGroup = DependsOnGroup,
        WaitForDependsOnMilliseconds = WaitForDependsOnMilliseconds,
        RunAsDomain = RunAsDomain,
        RunAsUser = RunAsUser,
        RunAsPassword = RunAsPassword,
        RunStatus = RunStatus,
        RunStatusTime = RunStatusTime,
        FrameworkJobStepID = FrameworkJobStepID,
        FrameworkJobID = FrameworkJobID,
        CreatedDate = CreatedDate,
        StartDate = StartDate,
        EndDate = EndDate,
        ExitMessage = ExitMessage,
        Active = Active,
    };
}
