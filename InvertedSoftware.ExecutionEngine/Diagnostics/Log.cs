// Copyright (c) Inverted Software. All rights reserved.

using Microsoft.Extensions.Logging;

namespace InvertedSoftware.WorkflowEngine.Diagnostics;

/// <summary>
/// Source-generated structured log messages. The compiler produces zero-alloc
/// strongly-typed log methods at build time.
/// </summary>
internal static partial class Log
{
    // ---- Framework lifecycle ------------------------------------------------

    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "Workflow framework started for job '{JobName}'")]
    public static partial void FrameworkStarted(ILogger logger, string jobName);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
        Message = "Workflow framework stopping for job '{JobName}' (soft={SoftExit})")]
    public static partial void FrameworkStopping(ILogger logger, string jobName, bool softExit);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information,
        Message = "Workflow framework stopped for job '{JobName}'")]
    public static partial void FrameworkStopped(ILogger logger, string jobName);

    // ---- Job lifecycle ------------------------------------------------------

    [LoggerMessage(EventId = 2000, Level = LogLevel.Debug,
        Message = "Job '{JobName}' message {MessageId} (jobId={JobId}) received")]
    public static partial void JobReceived(ILogger logger, string jobName, string? messageId, int jobId);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information,
        Message = "Job '{JobName}' jobId={JobId} completed in {ElapsedMs}ms")]
    public static partial void JobCompleted(ILogger logger, string jobName, int jobId, long elapsedMs);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning,
        Message = "Job '{JobName}' jobId={JobId} timed out after {ElapsedMs}ms (limit={LimitMs}ms)")]
    public static partial void JobTimedOut(ILogger logger, string jobName, int jobId, long elapsedMs, int limitMs);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Error,
        Message = "Job '{JobName}' jobId={JobId} failed at step '{StepName}'")]
    public static partial void JobFailed(ILogger logger, Exception exception, string jobName, int jobId, string stepName);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Warning,
        Message = "Job '{JobName}' received an undeserializable message (type='{MessageType}'); routing to Poison")]
    public static partial void JobDeserializationFailed(ILogger logger, Exception exception, string jobName, string? messageType);

    // ---- Step lifecycle -----------------------------------------------------

    [LoggerMessage(EventId = 3000, Level = LogLevel.Debug,
        Message = "Step '{StepName}' of job '{JobName}' jobId={JobId} starting")]
    public static partial void StepStarting(ILogger logger, string jobName, int jobId, string stepName);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Debug,
        Message = "Step '{StepName}' of job '{JobName}' jobId={JobId} completed in {ElapsedMs}ms")]
    public static partial void StepCompleted(ILogger logger, string jobName, int jobId, string stepName, long elapsedMs);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning,
        Message = "Step '{StepName}' of job '{JobName}' jobId={JobId} failed (attempt {Attempt}/{MaxRetries})")]
    public static partial void StepFailed(ILogger logger, Exception exception, string jobName, int jobId, string stepName, int attempt, int maxRetries);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Information,
        Message = "Step '{StepName}' of job '{JobName}' jobId={JobId} skipped (idempotency: already completed)")]
    public static partial void StepSkippedIdempotent(ILogger logger, string jobName, int jobId, string stepName);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Error,
        Message = "Idempotency store ReleaseAsync failed for step '{StepName}' of job '{JobName}' jobId={JobId}; the step's claim may persist until TTL. Original step failure is being propagated.")]
    public static partial void IdempotencyReleaseFailed(ILogger logger, Exception exception, string jobName, int jobId, string stepName);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Error,
        Message = "Idempotency store TryClaimAsync failed for step '{StepName}' of job '{JobName}' jobId={JobId}; this is an infrastructure failure, NOT a step failure. The message will be requeued; verify the idempotency store is reachable.")]
    public static partial void IdempotencyClaimFailed(ILogger logger, Exception exception, string jobName, int jobId, string stepName);

    [LoggerMessage(EventId = 3006, Level = LogLevel.Error,
        Message = "Fire-and-forget step '{StepName}' of job '{JobName}' faulted with an unobserved exception. The job has already moved on; this failure is being surfaced only in logs.")]
    public static partial void FireAndForgetStepFaulted(ILogger logger, Exception exception, string jobName, string stepName);

    // ---- Publish ------------------------------------------------------------

    [LoggerMessage(EventId = 4000, Level = LogLevel.Debug,
        Message = "Publishing message {MessageId} for job '{JobName}' (jobId={JobId})")]
    public static partial void PublishingMessage(ILogger logger, string jobName, int jobId, string? messageId);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Error,
        Message = "Failed to publish message for job '{JobName}' (jobId={JobId})")]
    public static partial void PublishFailed(ILogger logger, Exception exception, string jobName, int jobId);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning,
        Message = "Best-effort publish to {Destination} for job '{JobName}' failed; the engine continued. Investigate broker reachability.")]
    public static partial void SecondaryPublishFailed(ILogger logger, Exception exception, string jobName, string destination);

    // ---- Multi-tier failover ------------------------------------------------

    [LoggerMessage(EventId = 5000, Level = LogLevel.Information,
        Message = "Consumer for job '{JobName}' bound to tier {Tier} (multi-queue failover)")]
    public static partial void ConsumingFromTier(ILogger logger, string jobName, int tier);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning,
        Message = "Consumer tier {Tier} for job '{JobName}' became unavailable; re-selecting")]
    public static partial void ConsumerTierUnavailable(ILogger logger, Exception exception, string jobName, int tier);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Debug,
        Message = "Producer for job '{JobName}' falling over from tier {FailedTier} to tier {NextTier}")]
    public static partial void ProducerTierFailover(ILogger logger, Exception exception, string jobName, int failedTier, int nextTier);
}
