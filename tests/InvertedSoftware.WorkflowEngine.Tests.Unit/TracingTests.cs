// Copyright (c) Inverted Software. All rights reserved.

using System.Diagnostics;
using InvertedSoftware.WorkflowEngine.Config;
using InvertedSoftware.WorkflowEngine.Diagnostics;
using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.Queue;
using InvertedSoftware.WorkflowEngine.Queue.InMemory;
using InvertedSoftware.WorkflowEngine.Queue.Serialization;
using InvertedSoftware.WorkflowEngine.Steps;
using Xunit;

namespace InvertedSoftware.WorkflowEngine.Tests.Unit;

/// <summary>
/// Verifies that the engine emits Activity spans and that the consumer
/// successfully picks up the producer's W3C TraceContext.
/// </summary>
public class TracingTests : IDisposable
{
    private const string Job = "TraceJob";
    private readonly ActivityListener _listener;
    private readonly List<Activity> _activities = new();

    public TracingTests()
    {
        // Subscribe to the engine's ActivitySource before any test runs.
        _listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == Telemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => { lock (_activities) _activities.Add(a); },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task Publish_Emits_Producer_Span_And_Injects_TraceParent()
    {
        await using var queue = new InMemoryQueueProvider();
        using var tmp = new TestWorkflowXml(Job, ("Echo", typeof(EchoStep).FullName!));

        var stepFactory = new TypeNameStepFactory().Register<EchoStep>(typeof(EchoStep).FullName!, () => new EchoStep());
        var host = new WorkflowEngineHost(queue, new JsonMessageSerializer(), stepFactory,
            new EngineOptions { FrameworkConfigLocation = tmp.Path });

        await FrameworkManager.AddFrameworkJobAsync(Job, new ExampleMessage { JobID = 99 });

        // Drain one message to see the headers.
        await foreach (var msg in queue.ConsumeAsync(Job, new ConsumeOptions { Prefetch = 1 }, cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token))
        {
            Assert.True(msg.Headers.TryGetValue(Telemetry.TraceHeaders.TraceParent, out var tp),
                "traceparent header was not injected on publish.");
            Assert.False(string.IsNullOrEmpty(tp));
            Assert.StartsWith("00-", tp); // W3C format
            await msg.AckAsync();
            break;
        }

        lock (_activities)
        {
            Assert.Contains(_activities, a =>
                a.OperationName == Telemetry.Activities.Publish &&
                a.Kind == ActivityKind.Producer &&
                a.GetTagItem(Telemetry.Tags.JobName)?.ToString() == Job);
        }
    }

    [Fact]
    public async Task Consumer_Links_To_Producer_TraceContext()
    {
        await using var queue = new InMemoryQueueProvider();
        using var tmp = new TestWorkflowXml(Job, ("Echo", typeof(EchoStep).FullName!));

        var stepFactory = new TypeNameStepFactory().Register<EchoStep>(typeof(EchoStep).FullName!, () => new EchoStep());
        var host = new WorkflowEngineHost(queue, new JsonMessageSerializer(), stepFactory,
            new EngineOptions { FrameworkConfigLocation = tmp.Path });

        // Start a parent span around the publish so producer + consumer should share a traceId.
        ActivityTraceId expectedTraceId;
        using (var parent = new Activity("test.parent").Start())
        {
            expectedTraceId = parent.TraceId;
            await FrameworkManager.AddFrameworkJobAsync(Job, new ExampleMessage { JobID = 1 });
        }

        using var processor = host.CreateProcessor();
        var consumerTask = Task.Run(() => processor.StartFrameworkAsync(Job));

        await WaitForAsync(() =>
        {
            lock (_activities)
                return _activities.Any(a => a.OperationName == Telemetry.Activities.Consume);
        }, TimeSpan.FromSeconds(3));

        await processor.StopFrameworkAsync(isSoftExit: true);
        try { await consumerTask; } catch { }

        Activity? consumeSpan;
        lock (_activities)
            consumeSpan = _activities.FirstOrDefault(a => a.OperationName == Telemetry.Activities.Consume);

        Assert.NotNull(consumeSpan);
        Assert.Equal(expectedTraceId, consumeSpan!.TraceId);
        Assert.Equal(ActivityKind.Consumer, consumeSpan.Kind);
    }

    private static async Task WaitForAsync(Func<bool> cond, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Condition not met within {timeout.TotalSeconds:F1}s.");
    }

    public sealed class EchoStep : IStep
    {
        public void RunStep(IWorkflowMessage message, CancellationToken cancellationToken) { }
        public void Dispose() { }
    }
}
