// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Queue;

/// <summary>Snapshot of the four destinations' availability for a job.</summary>
/// <param name="MainAvailable">Whether the Main queue is reachable.</param>
/// <param name="ErrorAvailable">Whether the Error queue is reachable.</param>
/// <param name="PoisonAvailable">Whether the Poison queue is reachable.</param>
/// <param name="CompletedAvailable">Whether the Completed queue is reachable.</param>
/// <param name="ApproximateMainDepth">
/// Best-effort message count for the Main queue. <c>null</c> when the broker
/// cannot report depth cheaply (e.g. Kafka before consumer-group assignment).
/// </param>
/// <param name="Diagnostic">Provider-specific diagnostic blob (cluster, host, etc.).</param>
public sealed record QueueHealth(
    bool MainAvailable,
    bool ErrorAvailable,
    bool PoisonAvailable,
    bool CompletedAvailable,
    long? ApproximateMainDepth,
    string? Diagnostic);
