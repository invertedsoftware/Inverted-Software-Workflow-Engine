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

    [Fact]
    public async Task InMemoryStore_Release_Allows_Reclaim()
    {
        // Direct unit test for the store: a claim that's released (not marked completed)
        // must be re-claimable.
        var store = new InMemoryIdempotencyStore();
        var claim = new IdempotencyClaim(Job, "Step", 1);

        Assert.True(await store.TryClaimAsync(claim));
        await store.ReleaseAsync(claim);
        Assert.True(await store.TryClaimAsync(claim));    // can claim again — not completed
        await store.MarkCompletedAsync(claim);
        Assert.False(await store.TryClaimAsync(claim));   // now completed — blocked
    }

    [Fact]
    public async Task Failed_Step_Releases_Claim_So_Redelivery_Retries()
    {
        // Regression test for Bug 50: a step that throws on its first attempt must
        // release (not mark-completed) its idempotency claim, so a redelivery of the
        // same message can re-attempt the step. Otherwise consumer-crash recovery and
        // OnError=RetryJob silently turn step failures into "success".
        var step = new FailOnceStep();
        var store = new InMemoryIdempotencyStore();

        using var tmp = new TestWorkflowXml(Job, ("FailOnce", typeof(FailOnceStep).FullName!));
        var stepFactory = new TypeNameStepFactory().Register<FailOnceStep>(typeof(FailOnceStep).FullName!, () => step);

        await using var queue = new InMemoryQueueProvider();
        var host = new WorkflowEngineHost(queue, new JsonMessageSerializer(), stepFactory,
            new EngineOptions { FrameworkConfigLocation = tmp.Path }, idempotencyStore: store);

        using var processor = host.CreateProcessor();
        var consumerTask = Task.Run(() => processor.StartFrameworkAsync(Job));

        // First delivery — step throws, claim should be RELEASED.
        await FrameworkManager.AddFrameworkJobAsync(Job, new ExampleMessage { JobID = 99 });
        await WaitForAsync(() => step.AttemptCount >= 1, TimeSpan.FromSeconds(3));
        // Let the failure path complete (release the claim, ack/error-publish).
        await Task.Delay(200);

        // Redelivery — step should be re-attempted (and now succeeds).
        await FrameworkManager.AddFrameworkJobAsync(Job, new ExampleMessage { JobID = 99 });
        await WaitForAsync(() => step.AttemptCount >= 2, TimeSpan.FromSeconds(3));

        await processor.StopFrameworkAsync(isSoftExit: true);
        try { await consumerTask; } catch { }

        Assert.Equal(2, step.AttemptCount);
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

    public sealed class FailOnceStep : IStep
    {
        private int _attempts;
        public int AttemptCount => Volatile.Read(ref _attempts);
        public void RunStep(IWorkflowMessage message, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt == 1) throw new InvalidOperationException("transient failure");
        }
        public void Dispose() { }
    }
}
