// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Queue;

/// <summary>
/// Transport-agnostic surface for publishing and consuming workflow messages.
/// Concrete implementations exist for RabbitMQ, Kafka, Azure Service Bus, and
/// an in-memory transport used in tests and the console sample.
///
/// <para>The <c>tier</c> parameter on <see cref="CheckHealthAsync"/> and
/// <see cref="ConsumeAsync"/> selects which physical destination is targeted
/// for jobs that declare multiple <c>&lt;Queue&gt;</c> entries in
/// <c>Workflow.xml</c>. Tier 0 is the primary; higher tiers are fallbacks.
/// Single-queue jobs always pass tier 0 (the default), which means existing
/// provider <c>Mappings</c> configurations need no changes.</para>
/// </summary>
public interface IQueueProvider : IAsyncDisposable
{
    /// <summary>Stable identifier (e.g. <c>"RabbitMq"</c>, <c>"Kafka"</c>, <c>"AzureServiceBus"</c>, <c>"InMemory"</c>).</summary>
    string Name { get; }

    /// <summary>
    /// Probe broker reachability and per-destination state for the named job
    /// at the given <paramref name="tier"/>. Replaces MSMQ's
    /// <c>Peek(TimeSpan.Zero)</c> + <c>IOTimeout</c> dance, and is the
    /// mechanism the engine uses to pick the best tier under multi-queue failover.
    /// </summary>
    ValueTask<QueueHealth> CheckHealthAsync(string jobName, int tier = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publish a single message. The call does not return until the broker has
    /// durably acknowledged the publish (RabbitMQ publisher confirm, Kafka
    /// <c>acks=all</c>, Azure Service Bus <c>SendMessageAsync</c>).
    /// </summary>
    ValueTask PublishAsync(
        LogicalQueue destination,
        ReadOnlyMemory<byte> body,
        MessageHeaders headers,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publish multiple messages, atomic-where-possible. Implementations that
    /// support broker transactions (RabbitMQ <c>tx.select</c>, Kafka transactional
    /// producer, single-namespace ASB batch) make the batch atomic; others fall
    /// back to sequential best-effort and document the loss in their XML doc.
    /// </summary>
    ValueTask PublishBatchAsync(
        IReadOnlyList<OutgoingMessage> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Begin consuming the Main queue for the named job at the given
    /// <paramref name="tier"/>. The returned stream is driven by
    /// <c>await foreach</c>; the enumerator yields one
    /// <see cref="IReceivedMessage"/> at a time. The caller MUST ack or nack
    /// each yielded message. Disposing the enumerator stops the consumer; any
    /// in-flight unacked messages are nacked with <c>requeue: true</c>.
    /// </summary>
    IAsyncEnumerable<IReceivedMessage> ConsumeAsync(
        string jobName,
        ConsumeOptions options,
        int tier = 0,
        CancellationToken cancellationToken = default);
}
