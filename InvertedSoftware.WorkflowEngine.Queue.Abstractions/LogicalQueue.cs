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
/// </summary>
/// <param name="JobName">The job this queue belongs to (matches <c>ProcessorJob.JobName</c>).</param>
/// <param name="Kind">The role the queue plays.</param>
public readonly record struct LogicalQueue(string JobName, LogicalQueueKind Kind)
{
    public override string ToString() => $"{JobName}:{Kind}";

    /// <summary>The provider lookup key, e.g. <c>"ExampleJob:Main"</c>.</summary>
    public string MappingKey => $"{JobName}:{Kind}";
}
