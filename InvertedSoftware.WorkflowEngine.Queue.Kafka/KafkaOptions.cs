// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Queue.Kafka;

/// <summary>Apache Kafka provider configuration.</summary>
public sealed class KafkaOptions : QueueProviderOptions
{
    public override QueueProviderKind Kind => QueueProviderKind.Kafka;

    /// <summary>Comma-separated <c>host:port</c> list (the librdkafka <c>bootstrap.servers</c> value).</summary>
    public string BootstrapServers { get; set; } = "localhost:9092";

    /// <summary><c>Plaintext</c>, <c>Ssl</c>, <c>SaslPlaintext</c>, or <c>SaslSsl</c>.</summary>
    public string SecurityProtocol { get; set; } = "Plaintext";

    public string? SaslMechanism { get; set; }
    public string? SaslUsername { get; set; }
    public string? SaslPassword { get; set; }

    /// <summary>Per-(job, kind) physical mapping.</summary>
    public Dictionary<string, KafkaDestination> Mappings { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Required for <see cref="IQueueProvider.PublishBatchAsync"/> atomic semantics.</summary>
    public string? TransactionalId { get; set; }

    public bool EnableIdempotence { get; set; } = true;
    public string Acks { get; set; } = "All";
    public string AutoOffsetReset { get; set; } = "Earliest";

    /// <summary>Inactivity timeout for the consumer poll loop.</summary>
    public TimeSpan ConsumeTimeout { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Physical Kafka destination. The <see cref="ConsumerGroup"/> is required on
/// Main destinations and ignored on Error/Poison/Completed (those are publish-only
/// for the engine's purposes).
/// </summary>
public sealed record KafkaDestination(string Topic, string? ConsumerGroup = null);
