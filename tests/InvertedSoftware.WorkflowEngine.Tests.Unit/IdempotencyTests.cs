// Copyright (c) Inverted Software. All rights reserved.

using InvertedSoftware.WorkflowEngine.Config;
using InvertedSoftware.WorkflowEngine.Idempotency;
using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.Queue.InMemory;
using InvertedSoftware.WorkflowEngine.Queue.Serialization;
using InvertedSoftware.WorkflowEngine.Steps;
using Xunit;

namespace InvertedSoftware.WorkflowEngine.Tests.Unit;

public class IdempotencyTests
{
    private const string Job = "IdempotencyJob";

    [Fact]
    public async Task Step_Skipped_When_IdempotencyStore_Reports_Already_Completed()
    {
        var counter = new CountingStep();
        var store = new InMemoryIdempotencyStore();

        // Pre-seed: claim 1 is already completed → engine should skip it on every redelivery.
        await store.TryClaimAsync(new IdempotencyClaim(Job, "Counter", 42));
        await store.MarkCompletedAsync(new IdempotencyClaim(Job, "Counter", 42));

        using var tmp = new TestWorkflowXml(Job, ("Counter", typeof(CountingStep).FullName!));
        var stepFactory = new TypeNameStepFactory().Register<CountingStep>(typeof(CountingStep).FullName!, () => counter);

        await using var queue = new InMemoryQueueProvider();
        var host = new WorkflowEngineHost(queue, new JsonMessageSerializer(), stepFactory,
            new EngineOptions { FrameworkConfigLocation = tmp.Path }, idempotencyStore: store);

        using var processor = host.CreateProcessor();
        var consumerTask = Task.Run(() => processor.StartFrameworkAsync(Job));

        await FrameworkManager.AddFrameworkJobAsync(Job, new ExampleMessage { JobID = 42 });
        await FrameworkManager.AddFrameworkJobAsync(Job, new ExampleMessage { JobID = 42 }); // duplicate
        await FrameworkManager.AddFrameworkJobAsync(Job, new ExampleMessage { JobID = 100 }); // fresh

        await WaitForAsync(() => counter.ExecutionCount >= 1, TimeSpan.FromSeconds(3));
        // Give the system a moment to process all three.
        await Task.Delay(300);

        await processor.StopFrameworkAsync(isSoftExit: true);
        try { await consumerTask; } catch { }

        // Only the fresh jobId=100 should have executed; jobId=42 was already-completed (twice).
        Assert.Equal(1, counter.ExecutionCount);
    }

    [Fact]
    public async Task NoOp_Store_Allows_All_Executions()
    {
        var counter = new CountingStep();
        using var tmp = new TestWorkflowXml(Job, ("Counter", typeof(CountingStep).FullName!));
        var stepFactory = new TypeNameStepFactory().Register<CountingStep>(typeof(CountingStep).FullName!, () => counter);

        await using var queue = new InMemoryQueueProvider();
        var host = new WorkflowEngineHost(queue, new JsonMessageSerializer(), stepFactory,
            new EngineOptions { FrameworkConfigLocation = tmp.Path }); // no idempotency store → NoOp

        using var processor = host.CreateProcessor();
        var consumerTask = Task.Run(() => processor.StartFrameworkAsync(Job));

        await FrameworkManager.AddFrameworkJobAsync(Job, new ExampleMessage { JobID = 1 });
        await FrameworkManager.AddFrameworkJobAsync(Job, new ExampleMessage { JobID = 1 });
        await FrameworkManager.AddFrameworkJobAsync(Job, new ExampleMessage { JobID = 1 });

        await WaitForAsync(() => counter.ExecutionCount >= 3, TimeSpan.FromSeconds(3));
        await processor.StopFrameworkAsync(isSoftExit: true);
        try { await consumerTask; } catch { }

        Assert.Equal(3, counter.ExecutionCount);
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

    public sealed class CountingStep : IStep
    {
        private int _count;
        public int ExecutionCount => Volatile.Read(ref _count);
        public void RunStep(IWorkflowMessage message, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _count);
        public void Dispose() { }
    }
}
