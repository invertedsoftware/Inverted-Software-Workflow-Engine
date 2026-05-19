// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.DataObjects;

public class ProcessorQueueChangedEventArgs : EventArgs
{
    public string NewMessageQueue { get; set; } = string.Empty;
    public string OldMessageQueue { get; set; } = string.Empty;
}
