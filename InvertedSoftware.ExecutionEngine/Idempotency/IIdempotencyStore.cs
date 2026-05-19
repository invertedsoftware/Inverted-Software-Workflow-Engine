// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Idempotency;

/// <summary>
/// Optional store for step-level idempotency. The engine consults this BEFORE
/// invoking a step so that — under at-least-once delivery — a step that has
/// already run for a given (job, step, jobId) tuple is skipped on redelivery.
///
/// <para>The default <see cref="NoOpIdempotencyStore"/> always allows execution.
/// Provide a Redis / SQL / Cosmos implementation in production if your steps
/// have non-idempotent side effects (sending emails, charging cards, etc.).</para>
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically try to claim the right to execute this step. Returns
    /// <c>true</c> if the caller should proceed; <c>false</c> if a prior
    /// execution already completed.
    /// </summary>
    /// <param name="claim">Identifies the step occurrence: job + step + job id.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>
    /// <c>true</c> when the step should run; <c>false</c> when it should be skipped
    /// (treat as already-completed).
    /// </returns>
    ValueTask<bool> TryClaimAsync(IdempotencyClaim claim, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a previously-claimed step as completed. Called by the engine after
    /// successful step execution. Future <see cref="TryClaimAsync"/> calls for the
    /// same claim MUST return <c>false</c>.
    /// </summary>
    ValueTask MarkCompletedAsync(IdempotencyClaim claim, CancellationToken cancellationToken = default);

    /// <summary>
    /// Release a previously-claimed step that did NOT complete successfully. Called by
    /// the engine when a step exhausts its retries or is otherwise abandoned without
    /// a successful run. After release, <see cref="TryClaimAsync"/> for the same claim
    /// MUST return <c>true</c> again so the next delivery / job-level retry can re-attempt.
    ///
    /// <para>The default implementation is a no-op: implementations that don't track
    /// in-flight claims separately from completed ones (e.g. the default <see cref="NoOpIdempotencyStore"/>)
    /// don't need to do anything here. Implementations with a TTL on claims (e.g. Redis
    /// <c>SET NX EX</c>) may also no-op and let the TTL expire — but releasing eagerly
    /// makes job-level retry feel snappier.</para>
    /// </summary>
    ValueTask ReleaseAsync(IdempotencyClaim claim, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

/// <summary>Identifies a single step occurrence within a job.</summary>
public readonly record struct IdempotencyClaim(string JobName, string StepName, int JobId)
{
    /// <summary>Composite key suitable for a Redis/SQL primary key.</summary>
    public string Key => $"{JobName}:{StepName}:{JobId}";
}
