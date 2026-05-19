// Copyright (c) Inverted Software. All rights reserved.

using InvertedSoftware.WorkflowEngine.Config;
using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.Queue;
using InvertedSoftware.WorkflowEngine.Queue.InMemory;
using InvertedSoftware.WorkflowEngine.Queue.Serialization;
using InvertedSoftware.WorkflowEngine.Steps;
using Xunit;

namespace InvertedSoftware.WorkflowEngine.Tests.Unit;

/// <summary>
/// Pins the behavioural contract for <see cref="Processor.StopFrameworkAsync"/>.
/// </summary>
public class ProcessorSoftExitTests
{
    private const string Job = "SoftExitJob";

    [Fact]
    public async Task SoftExit_Allows_InFlight_Job_To_Complete_Naturally()
    {
        // Arrange: workflow with a single slow step that records when it completes.
        var slowStep = new SlowStep(TimeSpan.FromMilliseconds(500));
        using var tmp = new TempWorkflowXml(Job);

        var stepFactory = new TypeNameStepFactory().Register<SlowStep>(typeof(SlowStep).FullName!, () => slowStep);
        await using var queue = new InMemoryQueueProvider();
        var host = new WorkflowEngineHost(queue, new JsonMessageSerializer(), stepFactory,
            new EngineOptions { FrameworkMaxThreads = 4, FrameworkConfigLocation = tmp.Path });

        using var processor = host.CreateProcessor();
        var consumerTask = Task.Run(() => processor.StartFrameworkAsync(Job));

        // Publish one message and let the consumer pick it up.
        await FrameworkManager.AddFrameworkJobAsync(Job, new ExampleMessage { JobID = 1 });
        await WaitForAsync(() => processor.JobsRunning == 1, TimeSpan.FromSeconds(2));

        // Act: soft-exit while the step is mid-flight. It MUST run to completion.
        await processor.StopFrameworkAsync(isSoftExit: true);
        await consumerTask;

        // Assert
        Assert.True(slowStep.CompletedNormally,
            "Soft exit should let an in-flight step finish; observed cancellation instead.");
        Assert.Equal(0, processor.JobsRunning);
    }

    [Fact]
    public async Task HardExit_Cancels_InFlight_Job()
    {
        var slowStep = new SlowStep(TimeSpan.FromSeconds(10));
        using var tmp = new TempWorkflowXml(Job);

        var stepFactory = new TypeNameStepFactory().Register<SlowStep>(typeof(SlowStep).FullName!, () => slowStep);
        await using var queue = new InMemoryQueueProvider();
        var host = new WorkflowEngineHost(queue, new JsonMessageSerializer(), stepFactory,
            new EngineOptions { FrameworkMaxThreads = 4, FrameworkConfigLocation = tmp.Path });

        using var processor = host.CreateProcessor();
        var consumerTask = Task.Run(() => processor.StartFrameworkAsync(Job));

        await FrameworkManager.AddFrameworkJobAsync(Job, new ExampleMessage { JobID = 1 });
        await WaitForAsync(() => processor.JobsRunning == 1, TimeSpan.FromSeconds(2));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await processor.StopFrameworkAsync(isSoftExit: false);
        sw.Stop();

        // Hard stop returns immediately — not after the 10s step would have finished.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"Hard exit took {sw.Elapsed.TotalSeconds:F1}s; should be near-instant.");
        Assert.False(slowStep.CompletedNormally);
    }

    /// <summary>Wait for a condition with a timeout, polling every 50ms.</summary>
    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Condition not met within {timeout.TotalSeconds:F1}s.");
    }

    private sealed class SlowStep : IStep
    {
        private readonly TimeSpan _duration;
        public bool CompletedNormally { get; private set; }
        public SlowStep(TimeSpan duration) => _duration = duration;
        public void RunStep(IWorkflowMessage message, CancellationToken cancellationToken)
        {
            // Cooperative wait: throws OperationCanceledException on hard-stop, completes naturally on soft.
            Task.Delay(_duration, cancellationToken).GetAwaiter().GetResult();
            CompletedNormally = true;
        }
        public void Dispose() { }
    }

    /// <summary>Materialise a minimal Workflow.xml in a temp file for the test job.</summary>
    private sealed class TempWorkflowXml : IDisposable
    {
        public string Path { get; }
        public TempWorkflowXml(string jobName)
        {
            Path = System.IO.Path.GetTempFileName();
            File.WriteAllText(Path,
                $"""
                <Workflow>
                    <Job Name="{jobName}" MessageClass="ExampleMessage" NotifyComplete="false" MaxRunTimeMilliseconds="60000" MessageQueueType="Transactional">
                        <Queues>
                            <Queue MessageQueue="{jobName}.Main" ErrorQueue="{jobName}.Error" PoisonQueue="{jobName}.Poison" CompletedQueue="{jobName}.Completed" MessageQueueType="Transactional" />
                        </Queues>
                        <Steps>
                            <Step Name="Slow" Group="g" InvokeClass="{typeof(SlowStep).FullName}" OnError="Skip" RetryTimes="0" RunMode="Synchronous" />
                        </Steps>
                    </Job>
                </Workflow>
                """);
        }
        public void Dispose() { try { File.Delete(Path); } catch { } }
    }
}
