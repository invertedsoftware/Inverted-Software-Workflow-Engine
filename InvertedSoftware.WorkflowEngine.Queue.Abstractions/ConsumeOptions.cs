// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Queue;

/// <summary>Tuning for <see cref="IQueueProvider.ConsumeAsync"/>.</summary>
public sealed record ConsumeOptions
{
    /// <summary>Max in-flight messages to prefetch. Maps to broker QoS / max.poll.records / PrefetchCount.</summary>
    public int Prefetch { get; init; } = 16;

    /// <summary>
    /// Maximum time the consumer may take to ack a message before the broker
    /// considers it abandoned and redelivers. Maps to ASB lock duration, RabbitMQ
    /// consumer timeout, or Kafka <c>max.poll.interval.ms</c>.
    /// </summary>
    public TimeSpan AckTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// When <c>true</c>, the provider auto-acknowledges messages on receive
    /// (fire-and-forget consumption). Equivalent to legacy
    /// <c>MessageQueueType.NonTransactional</c>.
    /// </summary>
    public bool AutoAck { get; init; } = false;

    /// <summary>Consumer group identifier (Kafka <c>group.id</c>, ASB subscription).</summary>
    public string? ConsumerGroup { get; init; }
}
