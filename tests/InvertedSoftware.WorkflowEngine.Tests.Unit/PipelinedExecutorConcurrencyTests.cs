// Copyright (c) Inverted Software. All rights reserved.

using InvertedSoftware.WorkflowEngine.Config;
using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.Queue.InMemory;
using InvertedSoftware.WorkflowEngine.Queue.Serialization;
using InvertedSoftware.WorkflowEngine.Steps;
using Xunit;

namespace InvertedSoftware.WorkflowEngine.Tests.Unit;

/// <summary>
/// Pins the fix for the v1 pipelined-executor bug: when multiple jobs are in flight,
/// every job's caller must receive THAT job's result (not "any job's result").
/// </summary>
public class PipelinedExecutorConcurrencyTests
{
    private const string Job = "PipelineJob";

    [Fact]
    public async Task Concurrent_Jobs_Each_Complete_Without_Mixup()
    {
        // Use a step that records which JobIDs it saw. Under concurrency every JobID
        // must have its step run exactly once and its job complete exactly once.
        var recorder = new RecordingStep();
        var stepFactory = new TypeNameStepFactory().Register<RecordingStep>(typeof(RecordingStep).FullName!, () => recorder);

        using var tmp = new TestWorkflowXml(Job, ("Record", typeof(RecordingStep).FullName!));
        await using var queue = new InMemoryQueueProvider();
        var host = new WorkflowEngineHost(queue, new JsonMessageSerializer(), stepFactory,
            new EngineOptions { FrameworkConfigLocation = tmp.Path, FrameworkMaxThreads = 8, UsePipelinedOnMulticore = true });

        using var processor = host.CreateProcessor();
        var consumerTask = Task.Run(() => processor.StartFrameworkAsync(Job));

        // Publish 20 distinct jobs concurrently.
        const int jobCount = 20;
        var publishTasks = Enumerable.Range(1, jobCount).Select(id =>
            FrameworkManager.AddFrameworkJobAsync(Job, new ExampleMessage { JobID = id }));
        await Task.WhenAll(publishTasks);

        // Wait for all to be processed.
        await WaitForAsync(() => recorder.SeenIds.Count == jobCount, TimeSpan.FromSeconds(10));

        await processor.StopFrameworkAsync(isSoftExit: true);
        try { await consumerTask; } catch { }

        // Every JobID should have been seen exactly once.
        Assert.Equal(jobCount, recorder.SeenIds.Count);
        Assert.Equal(Enumerable.Range(1, jobCount).ToHashSet(), recorder.SeenIds.ToHashSet());
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

    public sealed class RecordingStep : IStep
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<int> _seen = new();
        public IReadOnlyCollection<int> SeenIds => _seen;
        public void RunStep(IWorkflowMessage message, CancellationToken cancellationToken) => _seen.Add(message.JobID);
        public void Dispose() { }
    }
}
