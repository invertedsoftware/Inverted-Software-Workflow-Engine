// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Queue;

/// <summary>
/// A message delivered by a provider. The consumer MUST call either
/// <see cref="AckAsync"/> or <see cref="NackAsync"/> exactly once.
/// </summary>
public interface IReceivedMessage
{
    /// <summary>Raw payload bytes. Decode using an <see cref="IMessageSerializer"/>.</summary>
    ReadOnlyMemory<byte> Body { get; }

    /// <summary>Per-message headers (including <see cref="MessageHeaders.MessageType"/>).</summary>
    MessageHeaders Headers { get; }

    /// <summary>The logical queue the message was received from.</summary>
    LogicalQueue Source { get; }

    /// <summary>Broker-specific delivery handle. Opaque to callers; required for ack/nack.</summary>
    object DeliveryTag { get; }

    /// <summary>Acknowledge successful processing. Removes the message from the broker.</summary>
    ValueTask AckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Negative-acknowledge. With <paramref name="requeue"/>=<c>true</c>, the message
    /// is returned to the broker for redelivery; with <c>false</c>, it is dropped or
    /// dead-lettered per provider semantics.
    /// </summary>
    ValueTask NackAsync(bool requeue, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deserialize the body into the type named by <see cref="MessageHeaders.MessageType"/>,
    /// using the supplied serializer.
    /// </summary>
    object DeserializeBody(IMessageSerializer serializer);

    /// <summary>Deserialize the body into <typeparamref name="T"/>.</summary>
    T DeserializeBody<T>(IMessageSerializer serializer);
}
