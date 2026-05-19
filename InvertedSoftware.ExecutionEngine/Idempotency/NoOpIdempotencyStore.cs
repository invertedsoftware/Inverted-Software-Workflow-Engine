// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Idempotency;

/// <summary>
/// Default <see cref="IIdempotencyStore"/>. Always allows execution. Use this when
/// steps are naturally idempotent (e.g. read-only operations, set-not-add database
/// upserts) and an external dedup store would be over-engineering.
/// </summary>
public sealed class NoOpIdempotencyStore : IIdempotencyStore
{
    public static readonly NoOpIdempotencyStore Instance = new();
    private NoOpIdempotencyStore() { }

    public ValueTask<bool> TryClaimAsync(IdempotencyClaim claim, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(true);

    public ValueTask MarkCompletedAsync(IdempotencyClaim claim, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

/// <summary>
/// In-memory <see cref="IIdempotencyStore"/>. Survives only for the process lifetime
/// — useful in unit tests and single-process demos, NOT for production deployments
/// (which need durable shared state across consumer instances).
/// </summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _completed = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _claimed = new(StringComparer.Ordinal);

    public ValueTask<bool> TryClaimAsync(IdempotencyClaim claim, CancellationToken cancellationToken = default)
    {
        if (_completed.ContainsKey(claim.Key)) return ValueTask.FromResult(false);
        // Allow re-entry for retries on the same node (claimed but not yet completed).
        _claimed.TryAdd(claim.Key, 0);
        return ValueTask.FromResult(true);
    }

    public ValueTask MarkCompletedAsync(IdempotencyClaim claim, CancellationToken cancellationToken = default)
    {
        _completed[claim.Key] = 0;
        _claimed.TryRemove(claim.Key, out _);
        return ValueTask.CompletedTask;
    }
}
