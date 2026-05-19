// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.DataObjects;

/// <summary>
/// Acknowledgement mode for the queue. <see cref="Transactional"/> = ack on success,
/// <see cref="NonTransactional"/> = auto-ack on receive (fire-and-forget). The names
/// are preserved for backwards compatibility with the v1 Workflow.xml schema.
/// </summary>
public enum MessageQueueType
{
    Transactional,
    NonTransactional,
}

/// <summary>
/// The operation that is about to be performed on the queue.
/// Preserved for backwards-compat with the v1 Workflow.xml schema; no longer
/// drives provider selection (failover is the provider's responsibility now).
/// </summary>
public enum QueueOperationType
{
    Pickup,
    Delivery,
}

/// <summary>
/// A single framework job loaded from <c>Workflow.xml</c>.
/// </summary>
public class ProcessorJob
{
    #region Config
    /// <summary>The job name.</summary>
    public string JobName { get; set; } = string.Empty;

    /// <summary>The CLR type of the message class on this job's queue.</summary>
    public string MessageClass { get; set; } = string.Empty;

    /// <summary>Logical name of the main message queue.</summary>
    public string MessageQueue { get; set; } = string.Empty;

    /// <summary>Logical name of the error queue.</summary>
    public string ErrorQueue { get; set; } = string.Empty;

    /// <summary>Logical name of the poison queue.</summary>
    public string PoisonQueue { get; set; } = string.Empty;

    /// <summary>Logical name of the completed queue.</summary>
    public string CompletedQueue { get; set; } = string.Empty;

    /// <summary>Send the original message to the completed queue on success.</summary>
    public bool NotifyComplete { get; set; }

    /// <summary>Maximum runtime for a single job before cancellation fires. Default = 1 hour.</summary>
    public int MaxRunTimeMilliseconds { get; set; } = 3600000;

    /// <summary>Transactional (ack on success) vs non-transactional (auto-ack on receive).</summary>
    public MessageQueueType MessageQueueType { get; set; }

    /// <summary>All declared queues for this job, in declaration order.</summary>
    public List<ProcessorQueue> ProcessorQueues { get; set; } = new();
    #endregion

    #region Log
    public int FrameworkJobID { get; set; } = -1;
    public string Description { get; set; } = string.Empty;
    public string MessageData { get; set; } = string.Empty;
    public int CreatedBy { get; set; } = -1;
    public DateTime? CreatedDate { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string ExitMessage { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
    #endregion

    /// <summary>Ordered list of steps for this job.</summary>
    public List<ProcessorStep> WorkFlowSteps { get; set; } = new();

    /// <summary>
    /// Produce an independent copy of this job suitable for per-job mutation of step
    /// state. Replaces the BinaryFormatter-based <c>ICloneable.Clone()</c> implementation
    /// from v1; that mechanism is removed in .NET 9+.
    /// <para>
    /// Event subscribers on contained <see cref="ProcessorQueue"/> instances are NOT
    /// copied — handlers attached to the template config must not implicitly receive
    /// per-job events.
    /// </para>
    /// </summary>
    public ProcessorJob DeepCopy()
    {
        var copy = new ProcessorJob
        {
            JobName = JobName,
            MessageClass = MessageClass,
            MessageQueue = MessageQueue,
            ErrorQueue = ErrorQueue,
            PoisonQueue = PoisonQueue,
            CompletedQueue = CompletedQueue,
            NotifyComplete = NotifyComplete,
            MaxRunTimeMilliseconds = MaxRunTimeMilliseconds,
            MessageQueueType = MessageQueueType,
            FrameworkJobID = FrameworkJobID,
            Description = Description,
            MessageData = MessageData,
            CreatedBy = CreatedBy,
            CreatedDate = CreatedDate,
            StartDate = StartDate,
            EndDate = EndDate,
            ExitMessage = ExitMessage,
            Active = Active,
        };
        copy.ProcessorQueues.Capacity = ProcessorQueues.Count;
        foreach (var q in ProcessorQueues)
            copy.ProcessorQueues.Add(q.DeepCopy());
        copy.WorkFlowSteps.Capacity = WorkFlowSteps.Count;
        foreach (var s in WorkFlowSteps)
            copy.WorkFlowSteps.Add(s.DeepCopy());
        return copy;
    }
}
