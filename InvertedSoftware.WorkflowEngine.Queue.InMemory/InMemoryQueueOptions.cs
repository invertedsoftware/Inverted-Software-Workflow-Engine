// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Queue.InMemory;

/// <summary>Options for <see cref="InMemoryQueueProvider"/>.</summary>
public sealed class InMemoryQueueOptions : QueueProviderOptions
{
    public override QueueProviderKind Kind => QueueProviderKind.InMemory;

    /// <summary>Capacity per logical queue (bounded). Defaults to 1024.</summary>
    public int QueueCapacity { get; set; } = 1024;
}
