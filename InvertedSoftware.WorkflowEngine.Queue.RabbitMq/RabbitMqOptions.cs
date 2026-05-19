// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Queue.RabbitMq;

/// <summary>
/// RabbitMQ-specific configuration. Bound from the
/// <c>WorkflowEngine:RabbitMq</c> section of <c>appsettings.json</c>.
/// </summary>
public sealed class RabbitMqOptions : QueueProviderOptions
{
    public override QueueProviderKind Kind => QueueProviderKind.RabbitMq;

    /// <summary>Primary connection string (AMQP URI), followed by failover hosts.</summary>
    public List<string> ConnectionStrings { get; init; } = new();

    /// <summary>Per-(job, logical-queue-kind) mapping to broker resources.</summary>
    public Dictionary<string, RabbitMqDestination> Mappings { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Enable publisher confirms. Strongly recommended.</summary>
    public bool PublisherConfirms { get; init; } = true;

    /// <summary>Maximum unacked messages per consumer channel.</summary>
    public ushort Prefetch { get; init; } = 16;

    /// <summary>Declare exchanges/queues/bindings on startup. Set to false if topology is managed externally.</summary>
    public bool DeclareTopologyOnStartup { get; init; } = true;
}

/// <summary>
/// Physical RabbitMQ destination for one <see cref="LogicalQueue"/>.
/// </summary>
/// <param name="Exchange">Exchange name (publish target).</param>
/// <param name="RoutingKey">Routing key. May be empty for fanout exchanges.</param>
/// <param name="Queue">Queue name (consume target, also bound to the exchange).</param>
public sealed record RabbitMqDestination(string Exchange, string RoutingKey, string Queue);
