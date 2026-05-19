// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Queue;

/// <summary>Base class for all provider-surface errors.</summary>
public class QueueProviderException : Exception
{
    public QueueProviderException() { }
    public QueueProviderException(string message) : base(message) { }
    public QueueProviderException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The broker (or destination) is unreachable. Triggers failover.</summary>
public sealed class QueueUnavailableException : QueueProviderException
{
    public QueueUnavailableException(string message) : base(message) { }
    public QueueUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The body could not be deserialized into the declared <c>MessageType</c>.</summary>
public sealed class MessageDeserializationException : QueueProviderException
{
    public MessageDeserializationException(string message) : base(message) { }
    public MessageDeserializationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The consumer lost its lock/lease before acking.</summary>
public sealed class AckTimeoutException : QueueProviderException
{
    public AckTimeoutException(string message) : base(message) { }
    public AckTimeoutException(string message, Exception innerException) : base(message, innerException) { }
}
