// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Queue;

/// <summary>An outbound message bundled for <see cref="IQueueProvider.PublishBatchAsync"/>.</summary>
public sealed record OutgoingMessage(
    LogicalQueue Destination,
    ReadOnlyMemory<byte> Body,
    MessageHeaders Headers);
