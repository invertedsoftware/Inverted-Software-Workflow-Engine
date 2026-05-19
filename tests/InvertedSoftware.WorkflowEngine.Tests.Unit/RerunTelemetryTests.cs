// Copyright (c) Inverted Software. All rights reserved.

using System.Diagnostics;
using InvertedSoftware.WorkflowEngine.Config;
using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.Queue;
using InvertedSoftware.WorkflowEngine.Queue.InMemory;
using InvertedSoftware.WorkflowEngine.Queue.Serialization;
using InvertedSoftware.WorkflowEngine.Steps;
using Xunit;

namespace InvertedSoftware.WorkflowEngine.Tests.Unit;

/// <summary>
/// Pins that rerun jobs are tagged with the ABSOLUTE JobID and an
/// <c>workflow.is_rerun</c> flag, not a confusing negative JobID.
/// </summary>
public class RerunTelemetryTests : IDisposable
{
    private const string Job = "RerunJob";
    private readonly ActivityListener _listener;
    private readonly List<Activity> _activities = new();

    public RerunTelemetryTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == Telemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { lock (_activities) _activities.Add(a); },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task ReAdd_Tags_AbsoluteJobId_And_IsRerun_Flag()
    {
        using var tmp = new TestWorkflowXml(Job, ("Echo", typeof(EchoStep).FullName!));
        await using var queue = new InMemoryQueueProvider();
        var stepFactory = new TypeNameStepFactory().Register<EchoStep>(typeof(EchoStep).FullName!, () => new EchoStep());
        var host = new WorkflowEngineHost(queue, new JsonMessageSerializer(), stepFactory,
            new EngineOptions { FrameworkConfigLocation = tmp.Path });

        // Producer-side: ReAddFrameworkJobAsync flips JobID sign internally.
        await FrameworkManager.ReAddFrameworkJobAsync(Job, new ExampleMessage { JobID = 42 });

        using var processor = host.CreateProcessor();
        var consumerTask = Task.Run(() => processor.StartFrameworkAsync(Job));
        await WaitForAsync(() =>
        {
            lock (_activities)
                return _activities.Any(a => a.OperationName == Telemetry.Activities.Consume);
        }, TimeSpan.FromSeconds(3));
        await processor.StopFrameworkAsync(isSoftExit: true);
        try { await consumerTask; } catch { }

        Activity publishSpan, consumeSpan;
        lock (_activities)
        {
            publishSpan = _activities.First(a => a.OperationName == Telemetry.Activities.Publish);
            consumeSpan = _activities.First(a => a.OperationName == Telemetry.Activities.Consume);
        }

        // Both spans should show the ABSOLUTE JobID, not the negated wire value.
        Assert.Equal(42, Convert.ToInt32(publishSpan.GetTagItem(Telemetry.Tags.JobId)));
        Assert.Equal(42, Convert.ToInt32(consumeSpan.GetTagItem(Telemetry.Tags.JobId)));

        // Both should carry is_rerun=true so operators can filter for it.
        Assert.Equal(true, publishSpan.GetTagItem("workflow.is_rerun"));
        Assert.Equal(true, consumeSpan.GetTagItem("workflow.is_rerun"));
    }

    private static async Task WaitForAsync(Func<bool> cond, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return;
            await Task.Delay(50);
        }
    }

    public sealed class EchoStep : IStep
    {
        public void RunStep(IWorkflowMessage message, CancellationToken cancellationToken) { }
        public void Dispose() { }
    }
}
