// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Queue;

/// <summary>
/// The logical role a queue plays for a given job.
/// </summary>
public enum LogicalQueueKind
{
    /// <summary>Producer-to-consumer main job queue.</summary>
    Main,
    /// <summary>Destination for <see cref="WorkflowErrorMessage"/>-style error summaries.</summary>
    Error,
    /// <summary>Destination for messages whose processing failed (the original body).</summary>
    Poison,
    /// <summary>Destination for messages whose job completed successfully (when NotifyComplete is enabled).</summary>
    Completed,
}

/// <summary>
/// A provider-agnostic identifier for one of the four destinations a job uses.
/// The provider maps this to a broker-native resource (RabbitMQ exchange/queue,
/// Kafka topic, Azure Service Bus queue/topic) via its <c>Mappings</c> option.
///
/// <para><b>Tiered failover (multi-queue).</b> A job may declare multiple
/// <c>&lt;Queue&gt;</c> entries in <c>Workflow.xml</c>; each entry becomes a
/// tier. Tier 0 is the primary (first declared); higher tiers are fallbacks.
/// The engine publishes producer messages to the lowest-tier (highest-priority)
/// available and consumes from the highest-tier with pending work first, so
/// backup queues drain stale work while the primary recovers — preserving the
/// v1 resilience pattern.</para>
///
/// <para>Mapping key format: <c>"JobName:Kind"</c> for tier 0 (back-compat with
/// single-queue jobs); <c>"JobName#N:Kind"</c> for tier <c>N &gt; 0</c>.</para>
/// </summary>
/// <param name="JobName">The job this queue belongs to (matches <c>ProcessorJob.JobName</c>).</param>
/// <param name="Kind">The role the queue plays.</param>
/// <param name="Tier">Tier index (0 = primary, 1+ = fallbacks in declaration order).</param>
public readonly record struct LogicalQueue(string JobName, LogicalQueueKind Kind, int Tier = 0)
{
    public override string ToString() =>
        Tier == 0 ? $"{JobName}:{Kind}" : $"{JobName}#{Tier}:{Kind}";

    /// <summary>
    /// Lookup key for the provider's Mappings dictionary. Tier 0 uses the bare
    /// <c>"JobName:Kind"</c> form so existing single-queue configurations need
    /// no changes.
    /// </summary>
    public string MappingKey => Tier == 0
        ? $"{JobName}:{Kind}"
        : $"{JobName}#{Tier}:{Kind}";
}
