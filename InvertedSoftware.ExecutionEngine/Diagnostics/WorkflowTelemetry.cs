// Copyright (c) Inverted Software. All rights reserved.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using InvertedSoftware.WorkflowEngine.Queue;

namespace InvertedSoftware.WorkflowEngine.Diagnostics;

/// <summary>
/// Process-wide telemetry instruments for the engine. Use these directly only if
/// you need to emit additional spans/metrics from custom code; for normal usage
/// the engine wires everything up automatically.
/// </summary>
public static class WorkflowTelemetry
{
    /// <summary>Engine-wide activity source. Subscribe via <c>AddSource("InvertedSoftware.WorkflowEngine")</c>.</summary>
    public static readonly ActivitySource ActivitySource = new(Telemetry.ActivitySourceName, "2.0.0");

    /// <summary>Engine-wide meter. Subscribe via <c>AddMeter("InvertedSoftware.WorkflowEngine")</c>.</summary>
    public static readonly Meter Meter = new(Telemetry.MeterName, "2.0.0");

    /// <summary>Counter: total jobs processed. Tags: job, outcome (complete|error|cancelled|deserialization_error|timeout).</summary>
    public static readonly Counter<long> JobsProcessed =
        Meter.CreateCounter<long>(Telemetry.Metrics.JobsProcessed, unit: "{job}", description: "Total jobs processed by the consumer.");

    /// <summary>Up-down counter: jobs currently in flight on this consumer. Tag: job.</summary>
    public static readonly UpDownCounter<long> JobsInFlight =
        Meter.CreateUpDownCounter<long>(Telemetry.Metrics.JobsInFlight, unit: "{job}", description: "Jobs currently executing.");

    /// <summary>Histogram: end-to-end job duration in seconds. Tags: job, outcome.</summary>
    public static readonly Histogram<double> JobDuration =
        Meter.CreateHistogram<double>(Telemetry.Metrics.JobDuration, unit: "s", description: "Job execution duration.");

    /// <summary>Histogram: per-step duration in seconds. Tags: job, step, outcome (complete|error|skipped).</summary>
    public static readonly Histogram<double> StepDuration =
        Meter.CreateHistogram<double>(Telemetry.Metrics.StepDuration, unit: "s", description: "Step execution duration.");

    /// <summary>Counter: error events. Tags: job, step (optional), kind (timeout|deserialization|step_failure|provider).</summary>
    public static readonly Counter<long> Errors =
        Meter.CreateCounter<long>(Telemetry.Metrics.Errors, unit: "{error}", description: "Engine-level error events.");
}
