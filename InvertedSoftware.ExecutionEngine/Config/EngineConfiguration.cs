// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Config;

/// <summary>
/// Engine-level runtime options. Pass an instance to the <c>Processor</c> ctor
/// or bind it from <c>IConfiguration</c> in your composition root.
/// </summary>
public sealed class EngineOptions
{
    /// <summary>Maximum concurrent in-flight jobs. Defaults to <see cref="Environment.ProcessorCount"/>.</summary>
    public int FrameworkMaxThreads { get; set; } = Environment.ProcessorCount;

    /// <summary>Use the TPL-Dataflow-based <c>PipelinedExecutor</c> when running on multi-core hosts.</summary>
    public bool UsePipelinedOnMulticore { get; set; } = true;

    /// <summary>Path to the workflow XML config file.</summary>
    public string FrameworkConfigLocation { get; set; } = "Config/Workflow.xml";

    /// <summary>
    /// For multi-tier jobs (multiple <c>&lt;Queue&gt;</c> entries in <c>Workflow.xml</c>),
    /// the consumer re-evaluates which tier to read from on this cadence so it
    /// can switch back to the primary when it recovers. Ignored for single-tier jobs.
    /// Default: 30 seconds. Minimum: 5 seconds.
    /// </summary>
    public int TierRebalanceIntervalSeconds { get; set; } = 30;
}
