// Copyright (c) Inverted Software. All rights reserved.

using InvertedSoftware.WorkflowEngine.Diagnostics;
using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.Queue;

namespace InvertedSoftware.WorkflowEngine;

/// <summary>
/// Helpers that translate engine-level "error" and "complete" signals into queue
/// publish operations. Failures on these secondary publishes are warned and
/// counted but never re-thrown, so they cannot mask the original step error.
/// Each publish targets the tier the consuming Processor is currently bound to,
/// so error/poison/complete messages colocate with where the work came from.
/// </summary>
internal static class QueueOperationsHandler
{
    /// <summary>
    /// Publish the original message body to the Poison queue and an
    /// <see cref="WorkflowErrorMessage"/> to the Error queue at the given
    /// <paramref name="tier"/>.
    /// </summary>
    internal static async Task HandleErrorAsync(
        WorkflowEngineHost host,
        string jobName,
        int tier,
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

        var batch = new[]
        {
            new OutgoingMessage(new LogicalQueue(jobName, LogicalQueueKind.Poison, tier), host.Serializer.Serialize(original), poisonHeaders),
            new OutgoingMessage(new LogicalQueue(jobName, LogicalQueueKind.Error,  tier), host.Serializer.Serialize(errorMessage), errorHeaders),
        };

        try
        {
            await host.QueueProvider.PublishBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (QueueProviderException e)
        {
            // Best-effort: don't mask the original step exception that triggered this handler.
            // We still surface the failure in logs + metrics so operators see broker outages.
            Log.SecondaryPublishFailed(host.CreateLogger<WorkflowEngineHost>(), e, jobName,
                tier == 0 ? "Error+Poison" : $"Error+Poison (tier {tier})");
            WorkflowTelemetry.Errors.Add(1,
                new KeyValuePair<string, object?>("job", jobName),
                new KeyValuePair<string, object?>("kind", "secondary_publish"));
        }
    }

    /// <summary>Publish the original message to the Completed queue at the given tier.</summary>
    internal static async Task HandleCompleteAsync(
        WorkflowEngineHost host,
        string jobName,
        int tier,
        IWorkflowMessage message,
        CancellationToken cancellationToken)
    {
        var headers = new MessageHeaders
        {
            ContentType = host.Serializer.ContentType,
            MessageType = message.GetType().FullName,
            CorrelationId = message.JobID.ToString(),
        };

        try
        {
            await host.QueueProvider.PublishAsync(
                new LogicalQueue(jobName, LogicalQueueKind.Completed, tier),
                host.Serializer.Serialize(message),
                headers,
                cancellationToken).ConfigureAwait(false);
        }
        catch (QueueProviderException e)
        {
            Log.SecondaryPublishFailed(host.CreateLogger<WorkflowEngineHost>(), e, jobName,
                tier == 0 ? "Completed" : $"Completed (tier {tier})");
            WorkflowTelemetry.Errors.Add(1,
                new KeyValuePair<string, object?>("job", jobName),
                new KeyValuePair<string, object?>("kind", "secondary_publish"));
        }
    }
}
