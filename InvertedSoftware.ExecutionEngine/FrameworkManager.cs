// Copyright (c) Inverted Software. All rights reserved.

using System.Diagnostics;
using InvertedSoftware.WorkflowEngine.Common.Exceptions;
using InvertedSoftware.WorkflowEngine.Diagnostics;
using InvertedSoftware.WorkflowEngine.DataObjects;
using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.Queue;
using Microsoft.Extensions.Logging;

namespace InvertedSoftware.WorkflowEngine;

/// <summary>
/// Producer-side facade for publishing jobs onto the main queue. Mirrors the v1
/// public surface (<c>AddFrameworkJob</c>, <c>ReAddFrameworkJob</c>) so existing
/// consumer code keeps compiling, but routes through <see cref="WorkflowEngineHost"/>.
/// </summary>
public static class FrameworkManager
{
    private static WorkflowEngineHost? _host;

    /// <summary>
    /// Bind the static facade to a <see cref="WorkflowEngineHost"/>. Called
    /// automatically by the host's constructor.
    /// </summary>
    public static void Configure(WorkflowEngineHost host) => _host = host;

    private static WorkflowEngineHost RequireHost() =>
        _host ?? throw new InvalidOperationException(
            "FrameworkManager has not been configured. Construct a WorkflowEngineHost first.");

    /// <summary>Publish a new job onto the main queue.</summary>
    public static Task AddFrameworkJobAsync(string jobName, IWorkflowMessage message, CancellationToken cancellationToken = default)
        => PublishAsync(jobName, message, isRerun: false, cancellationToken);

    /// <summary>
    /// Re-publish a job that ran in the past, negating <see cref="IWorkflowMessage.JobID"/>
    /// to signal "skip <c>DependsOn</c> checks".
    /// </summary>
    public static Task ReAddFrameworkJobAsync(string jobName, IWorkflowMessage message, CancellationToken cancellationToken = default)
        => PublishAsync(jobName, message, isRerun: true, cancellationToken);

    /// <summary>Synchronous wrapper for <see cref="AddFrameworkJobAsync"/>.</summary>
    public static void AddFrameworkJob(string jobName, IWorkflowMessage message)
        => AddFrameworkJobAsync(jobName, message).GetAwaiter().GetResult();

    /// <summary>Synchronous wrapper for <see cref="ReAddFrameworkJobAsync"/>.</summary>
    public static void ReAddFrameworkJob(string jobName, IWorkflowMessage message)
        => ReAddFrameworkJobAsync(jobName, message).GetAwaiter().GetResult();

    private static async Task PublishAsync(string jobName, IWorkflowMessage message, bool isRerun, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        ArgumentNullException.ThrowIfNull(message);

        var host = RequireHost();
        var logger = host.CreateLogger<WorkflowEngineHost>();

        // Open a producer-side span. The current Activity context (if any from an
        // ASP.NET request, a background job, etc.) becomes the parent.
        using var activity = WorkflowTelemetry.ActivitySource.StartActivity(
            Telemetry.Activities.Publish, ActivityKind.Producer);
        activity?.SetTag(Telemetry.Tags.JobName, jobName);
        activity?.SetTag(Telemetry.Tags.MessagingSystem, host.QueueProvider.Name);
        activity?.SetTag(Telemetry.Tags.MessagingOperation, "publish");

        var jobTemplate = new ProcessorJob { JobName = jobName, CreatedDate = DateTime.UtcNow };
        host.Configuration.LoadFrameworkConfig(jobTemplate);

        // Tag/log the absolute JobID with an explicit is_rerun flag so traces are readable.
        // The wire JobID is negative-on-rerun (engine convention for "skip DependsOn"), but
        // operators shouldn't have to know that to grep logs.
        var absoluteJobId = Math.Abs(message.JobID);
        activity?.SetTag(Telemetry.Tags.JobId, absoluteJobId);
        if (isRerun) activity?.SetTag("workflow.is_rerun", true);

        if (isRerun)
            message.JobID = -absoluteJobId;

        var messageId = Guid.NewGuid().ToString("N");
        var headers = new MessageHeaders
        {
            MessageId = messageId,
            CorrelationId = absoluteJobId.ToString(),
            ContentType = host.Serializer.ContentType,
            MessageType = ResolveMessageType(jobTemplate, message),
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
        };
        activity?.SetTag(Telemetry.Tags.MessageId, messageId);

        // Inject W3C TraceContext so the consumer can link its span to ours.
        InjectTraceContext(activity, headers);

        Log.PublishingMessage(logger, jobName, absoluteJobId, messageId);

        var body = host.Serializer.Serialize(message);

        try
        {
            await host.QueueProvider.PublishAsync(
                new LogicalQueue(jobName, LogicalQueueKind.Main),
                body,
                headers,
                cancellationToken).ConfigureAwait(false);
        }
        catch (QueueProviderException e)
        {
            activity?.SetStatus(ActivityStatusCode.Error, e.Message);
            WorkflowTelemetry.Errors.Add(1,
                new KeyValuePair<string, object?>("job", jobName),
                new KeyValuePair<string, object?>("kind", "provider"));
            Log.PublishFailed(logger, e, jobName, absoluteJobId);
            throw new WorkflowException("Error adding message to Queue", e);
        }
    }

    /// <summary>
    /// Prefer the configured MessageClass from Workflow.xml, but fall back to the
    /// runtime type if the config omits it (matches v1 behaviour).
    /// </summary>
    private static string ResolveMessageType(ProcessorJob job, IWorkflowMessage message)
    {
        if (!string.IsNullOrEmpty(job.MessageClass))
        {
            return job.MessageClass.Contains('.', StringComparison.Ordinal)
                ? job.MessageClass
                : $"InvertedSoftware.WorkflowEngine.Messages.{job.MessageClass}";
        }
        return message.GetType().FullName ?? message.GetType().Name;
    }

    private static void InjectTraceContext(Activity? activity, MessageHeaders headers)
    {
        if (activity is null) return;
        // W3C TraceContext: traceparent header is "00-<traceId>-<spanId>-<flags>"
        headers[Telemetry.TraceHeaders.TraceParent] = activity.Id ?? string.Empty;
        if (!string.IsNullOrEmpty(activity.TraceStateString))
            headers[Telemetry.TraceHeaders.TraceState] = activity.TraceStateString;
    }
}
