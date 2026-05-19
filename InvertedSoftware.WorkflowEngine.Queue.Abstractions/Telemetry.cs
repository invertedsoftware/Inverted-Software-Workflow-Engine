// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Queue;

/// <summary>
/// Stable identifiers for tracing, metrics, and headers. Use these constants when
/// configuring OpenTelemetry to avoid drift between your dashboards and the engine
/// internals.
/// </summary>
public static class Telemetry
{
    /// <summary>
    /// Name of the <see cref="System.Diagnostics.ActivitySource"/> the engine emits
    /// spans on. Subscribe with
    /// <c>tracerProviderBuilder.AddSource(Telemetry.ActivitySourceName)</c>.
    /// </summary>
    public const string ActivitySourceName = "InvertedSoftware.WorkflowEngine";

    /// <summary>
    /// Name of the <see cref="System.Diagnostics.Metrics.Meter"/> the engine emits
    /// metrics on. Subscribe with
    /// <c>meterProviderBuilder.AddMeter(Telemetry.MeterName)</c>.
    /// </summary>
    public const string MeterName = "InvertedSoftware.WorkflowEngine";

    /// <summary>Activity names emitted by the engine and providers.</summary>
    public static class Activities
    {
        public const string Publish = "workflow.publish";
        public const string Consume = "workflow.consume";
        public const string Step = "workflow.step";
    }

    /// <summary>Activity tag keys (compatible with OpenTelemetry messaging semconv).</summary>
    public static class Tags
    {
        public const string JobName = "workflow.job_name";
        public const string JobId = "workflow.job_id";
        public const string StepName = "workflow.step_name";
        public const string StepOutcome = "workflow.step_outcome";
        public const string MessageId = "messaging.message.id";
        public const string MessagingSystem = "messaging.system";
        public const string MessagingOperation = "messaging.operation";
        public const string MessagingDestination = "messaging.destination.name";
    }

    /// <summary>Metric instrument names.</summary>
    public static class Metrics
    {
        public const string JobsProcessed = "wf.jobs.processed";
        public const string JobsInFlight = "wf.jobs.in_flight";
        public const string JobDuration = "wf.job.duration";
        public const string StepDuration = "wf.step.duration";
        public const string Errors = "wf.errors";
    }

    /// <summary>W3C TraceContext headers. Standard names; do not change.</summary>
    public static class TraceHeaders
    {
        public const string TraceParent = "traceparent";
        public const string TraceState = "tracestate";
    }
}
