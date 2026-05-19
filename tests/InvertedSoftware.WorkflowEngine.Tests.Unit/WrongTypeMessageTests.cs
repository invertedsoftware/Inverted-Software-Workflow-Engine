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
/// Pins the fix for the silent-ack bug: a body whose CLR type doesn't implement
/// <see cref="IWorkflowMessage"/> must surface as a deserialization error (visible
/// span + error counter) and not be silently dropped.
/// </summary>
public class WrongTypeMessageTests : IDisposable
{
    private const string Job = "WrongTypeJob";
    private readonly ActivityListener _listener;
    private readonly List<Activity> _activities = new();

    public WrongTypeMessageTests()
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
    public async Task Body_Not_Implementing_IWorkflowMessage_Is_Treated_As_Deserialization_Error()
    {
        using var tmp = new TestWorkflowXml(Job, ("Echo", typeof(EchoStep).FullName!));
        await using var queue = new InMemoryQueueProvider();
        var serializer = new JsonMessageSerializer();
        var stepFactory = new TypeNameStepFactory().Register<EchoStep>(typeof(EchoStep).FullName!, () => new EchoStep());

        var host = new WorkflowEngineHost(queue, serializer, stepFactory,
            new EngineOptions { FrameworkConfigLocation = tmp.Path });

        // Push a message whose declared MessageType deserializes successfully but does
        // NOT implement IWorkflowMessage. With the pre-fix engine, the cast would throw
        // and the message would be silently ACKed.
        var badBody = serializer.Serialize(new NotAWorkflowMessage { Random = "x" });
        await queue.PublishAsync(
            new LogicalQueue(Job, LogicalQueueKind.Main),
            badBody,
            new MessageHeaders
            {
                ContentType = serializer.ContentType,
                MessageType = typeof(NotAWorkflowMessage).FullName,
                CorrelationId = "0",
            });

        using var processor = host.CreateProcessor();
        var consumerTask = Task.Run(() => processor.StartFrameworkAsync(Job));

        // Wait for the consume span to be stopped (visible in the listener).
        await WaitForAsync(() =>
        {
            lock (_activities) return _activities.Any(a => a.OperationName == Telemetry.Activities.Consume);
        }, TimeSpan.FromSeconds(3));

        await processor.StopFrameworkAsync(isSoftExit: true);
        try { await consumerTask; } catch { }

        // The consume span must exist, have Error status, and reference the wrong type.
        Activity? consumeSpan;
        lock (_activities)
            consumeSpan = _activities.FirstOrDefault(a => a.OperationName == Telemetry.Activities.Consume);

        Assert.NotNull(consumeSpan);
        Assert.Equal(ActivityStatusCode.Error, consumeSpan!.Status);
        Assert.False(string.IsNullOrEmpty(consumeSpan.StatusDescription),
            "The error status should carry a description naming the type mismatch.");
        Assert.Contains(nameof(NotAWorkflowMessage), consumeSpan.StatusDescription);
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

    /// <summary>Deserializes fine but does NOT implement <see cref="IWorkflowMessage"/>.</summary>
    public sealed class NotAWorkflowMessage
    {
        public string Random { get; set; } = string.Empty;
    }

    public sealed class EchoStep : IStep
    {
        public void RunStep(IWorkflowMessage message, CancellationToken cancellationToken) { }
        public void Dispose() { }
    }
}
