// Copyright (c) Inverted Software. All rights reserved.

using InvertedSoftware.WorkflowEngine.Diagnostics;
using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.Queue;

namespace InvertedSoftware.WorkflowEngine;

/// <summary>
/// Helpers that translate engine-level "error" and "complete" signals into queue
/// publish operations.
///
/// <para>Multi-tier fallback: each publish tries the consumer's current tier
/// first, then falls forward through the remaining tiers if the preferred tier
/// is unavailable — so error / poison / complete diagnostics survive the very
/// outage scenarios the multi-queue feature is designed for. Only after every
/// tier rejects the publish do we log a warning and swallow (preserving the
/// "best-effort, don't mask the original step exception" contract).</para>
/// </summary>
internal static class QueueOperationsHandler
{
    /// <summary>
    /// Publish the original message body to the Poison queue and an
    /// <see cref="WorkflowErrorMessage"/> to the Error queue. Tries the
    /// <paramref name="preferredTier"/> first, then walks the remaining tiers
    /// forward; gives up after all are exhausted.
    /// </summary>
    internal static async Task HandleErrorAsync(
        WorkflowEngineHost host,
        string jobName,
        int preferredTier,
        int tierCount,
        IWorkflowMessage original,
        WorkflowErrorMessage errorMessage,
        CancellationToken cancellationToken)
    {
        var originalType = original.GetType().FullName ?? original.GetType().Name;
        var poisonHeaders = new MessageHeaders
        {
            ContentType = host.Serializer.ContentType,
            MessageType = originalType,
            CorrelationId = original.JobID.ToString(),
        };
        var errorHeaders = new MessageHeaders
        {
            ContentType = host.Serializer.ContentType,
            MessageType = typeof(WorkflowErrorMessage).FullName,
            CorrelationId = original.JobID.ToString(),
        };

        QueueProviderException? lastFailure = null;
        foreach (var tier in EnumerateTiers(preferredTier, tierCount))
        {
            var batch = new[]
            {
                new OutgoingMessage(new LogicalQueue(jobName, LogicalQueueKind.Poison, tier), host.Serializer.Serialize(original), poisonHeaders),
                new OutgoingMessage(new LogicalQueue(jobName, LogicalQueueKind.Error,  tier), host.Serializer.Serialize(errorMessage), errorHeaders),
            };
            try
            {
                await host.QueueProvider.PublishBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                return; // success
            }
            catch (QueueUnavailableException e)
            {
                lastFailure = e;
                // Try next tier
            }
            catch (QueueProviderException e)
            {
                // Non-transient (e.g. mapping missing) — no point retrying other tiers.
                lastFailure = e;
                break;
            }
        }

        // Every tier rejected the publish. Surface in logs + metrics but swallow.
        if (lastFailure is not null)
        {
            Log.SecondaryPublishFailed(
                host.CreateLogger<WorkflowEngineHost>(),
                lastFailure,
                jobName,
                $"Error+Poison (all {tierCount} tier(s))");
            WorkflowTelemetry.Errors.Add(1,
                new KeyValuePair<string, object?>("job", jobName),
                new KeyValuePair<string, object?>("kind", "secondary_publish"));
        }
    }

    /// <summary>Publish the original message to the Completed queue with tier fallback.</summary>
    internal static async Task HandleCompleteAsync(
        WorkflowEngineHost host,
        string jobName,
        int preferredTier,
        int tierCount,
        IWorkflowMessage message,
        CancellationToken cancellationToken)
    {
        var headers = new MessageHeaders
        {
            ContentType = host.Serializer.ContentType,
            MessageType = message.GetType().FullName,
            CorrelationId = message.JobID.ToString(),
        };
        var body = host.Serializer.Serialize(message);

        QueueProviderException? lastFailure = null;
        foreach (var tier in EnumerateTiers(preferredTier, tierCount))
        {
            try
            {
                await host.QueueProvider.PublishAsync(
                    new LogicalQueue(jobName, LogicalQueueKind.Completed, tier),
                    body,
                    headers,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (QueueUnavailableException e)
            {
                lastFailure = e;
            }
            catch (QueueProviderException e)
            {
                lastFailure = e;
                break;
            }
        }

        if (lastFailure is not null)
        {
            Log.SecondaryPublishFailed(
                host.CreateLogger<WorkflowEngineHost>(),
                lastFailure,
                jobName,
                $"Completed (all {tierCount} tier(s))");
            WorkflowTelemetry.Errors.Add(1,
                new KeyValuePair<string, object?>("job", jobName),
                new KeyValuePair<string, object?>("kind", "secondary_publish"));
        }
    }

    /// <summary>
    /// Iterate tiers starting from <paramref name="preferred"/>, wrapping around
    /// so every tier in [0, count) is visited exactly once. Lets the engine try
    /// the consumer's current tier first while still falling forward through
    /// the rest on outage.
    /// </summary>
    private static IEnumerable<int> EnumerateTiers(int preferred, int count)
    {
        if (count <= 0) yield break;
        if (count == 1) { yield return 0; yield break; }
        if (preferred < 0 || preferred >= count) preferred = 0;

        yield return preferred;
        for (var t = 0; t < count; t++)
        {
            if (t == preferred) continue;
            yield return t;
        }
    }
}
