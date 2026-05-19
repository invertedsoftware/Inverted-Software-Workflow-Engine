// Copyright (c) Inverted Software. All rights reserved.

using InvertedSoftware.WorkflowEngine.Config;
using InvertedSoftware.WorkflowEngine.Idempotency;
using InvertedSoftware.WorkflowEngine.Queue;
using InvertedSoftware.WorkflowEngine.Steps;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace InvertedSoftware.WorkflowEngine;

/// <summary>
/// Top-level composition root. Bundles the configured queue provider, serializer,
/// step factory, engine options, and observability hooks. Use this to build
/// <see cref="Processor"/> instances and publish jobs without juggling DI yourself.
/// </summary>
public sealed class WorkflowEngineHost
{
    public IQueueProvider QueueProvider { get; }
    public IMessageSerializer Serializer { get; }
    public IStepFactory StepFactory { get; }
    public EngineOptions Options { get; }
    public WorkflowConfiguration Configuration { get; }
    public ILoggerFactory LoggerFactory { get; }
    public IIdempotencyStore IdempotencyStore { get; }

    public WorkflowEngineHost(
        IQueueProvider queueProvider,
        IMessageSerializer serializer,
        IStepFactory stepFactory,
        EngineOptions? options = null,
        ILoggerFactory? loggerFactory = null,
        IIdempotencyStore? idempotencyStore = null)
    {
        QueueProvider = queueProvider ?? throw new ArgumentNullException(nameof(queueProvider));
        Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        StepFactory = stepFactory ?? throw new ArgumentNullException(nameof(stepFactory));
        Options = options ?? new EngineOptions();
        Configuration = new WorkflowConfiguration(Options);
        LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        IdempotencyStore = idempotencyStore ?? NoOpIdempotencyStore.Instance;

        // Configure the static FrameworkManager facade so legacy consumer code keeps working.
        FrameworkManager.Configure(this);
    }

    /// <summary>Create a <see cref="Processor"/> ready to consume the named job.</summary>
    public Processor CreateProcessor() => new(this);

    internal ILogger<T> CreateLogger<T>() => LoggerFactory.CreateLogger<T>();
}
