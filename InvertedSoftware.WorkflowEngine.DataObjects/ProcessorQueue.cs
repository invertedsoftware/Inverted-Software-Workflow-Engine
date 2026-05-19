// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.DataObjects;

/// <summary>
/// One declared queue for a job. The four <c>*Queue</c> properties hold logical
/// names (e.g. <c>"ExampleJob.Main"</c>); the queue provider resolves these to
/// broker-native resources via its <c>Mappings</c> option.
/// </summary>
public class ProcessorQueue
{
    private string _messageQueue = string.Empty;

    /// <summary>Logical name of the main queue.</summary>
    public string MessageQueue
    {
        get => _messageQueue;
        set
        {
            if (value != _messageQueue)
                OnProcessorQueueChanged(new ProcessorQueueChangedEventArgs
                {
                    NewMessageQueue = value,
                    OldMessageQueue = _messageQueue,
                });
            _messageQueue = value;
        }
    }

    /// <summary>Logical name of the error queue.</summary>
    public string ErrorQueue { get; set; } = string.Empty;

    /// <summary>Logical name of the poison queue.</summary>
    public string PoisonQueue { get; set; } = string.Empty;

    /// <summary>Logical name of the completed queue.</summary>
    public string CompletedQueue { get; set; } = string.Empty;

    /// <summary>Acknowledgement mode for this queue.</summary>
    public MessageQueueType MessageQueueType { get; set; }

    #region Events
    public delegate void ProcessorQueueEventHandler(object sender, ProcessorQueueChangedEventArgs e);

    /// <summary>
    /// Raised when <see cref="MessageQueue"/> is changed. Note that the event invocation
    /// list is intentionally NOT copied by <see cref="DeepCopy"/>: subscribers attached
    /// to a configuration template must not implicitly receive per-job events.
    /// </summary>
    public event ProcessorQueueEventHandler? ProcessorQueueChanged;

    protected virtual void OnProcessorQueueChanged(ProcessorQueueChangedEventArgs e) =>
        ProcessorQueueChanged?.Invoke(this, e);
    #endregion

    /// <summary>Produce an independent copy without copying event subscribers.</summary>
    public ProcessorQueue DeepCopy() => new()
    {
        _messageQueue = _messageQueue,
        ErrorQueue = ErrorQueue,
        PoisonQueue = PoisonQueue,
        CompletedQueue = CompletedQueue,
        MessageQueueType = MessageQueueType,
    };
}
