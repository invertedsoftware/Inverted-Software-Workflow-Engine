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
/// Verifies the v1 multi-queue resilience semantic — producers iterate tiers
/// forward, consumers iterate in reverse and prefer tiers with pending work.
/// </summary>
public class MultiTierFailoverTests
{
    private const string Job = "TieredJob";

    [Fact]
    public async Task LogicalQueue_MappingKey_Format_Differentiates_Tiers()
    {
        Assert.Equal("Job:Main",    new LogicalQueue("Job", LogicalQueueKind.Main, 0).MappingKey);
        Assert.Equal("Job#1:Main",  new LogicalQueue("Job", LogicalQueueKind.Main, 1).MappingKey);
        Assert.Equal("Job#2:Error", new LogicalQueue("Job", LogicalQueueKind.Error, 2).MappingKey);
    }

    [Fact]
    public async Task Producer_Falls_Over_To_Tier1_When_Tier0_Is_Unavailable()
    {
        // Provider that fails publishes to tier 0 but accepts on tier 1+.
        var primaryFailing = new FailingTier0Provider();

        // Engine wired with a job that declares two tiers.
        using var tmp = new TwoTierWorkflowXml(Job);
        var stepFactory = new TypeNameStepFactory();
        var host = new WorkflowEngineHost(primaryFailing, new JsonMessageSerializer(), stepFactory,
            new EngineOptions { FrameworkConfigLocation = tmp.Path });

        await FrameworkManager.AddFrameworkJobAsync(Job, new ExampleMessage { JobID = 7 });

        Assert.Equal(1, primaryFailing.Tier0PublishAttempts);
        Assert.Equal(1, primaryFailing.Tier1PublishAttempts);
    }

    [Fact]
    public async Task Consumer_Picks_Higher_Tier_When_It_Has_Messages()
    {
        // Stage: tier 0 reachable but empty, tier 1 reachable and has 2 messages.
        // The consumer (reverse iteration) must pick tier 1.
        var probedTiers = new System.Collections.Concurrent.ConcurrentBag<int>();
        var provider = new TierHealthProbingProvider(
            mainDepthByTier: new Dictionary<int, long> { [0] = 0, [1] = 2 },
            onCheckHealth: tier => probedTiers.Add(tier));

        using var tmp = new TwoTierWorkflowXml(Job);
        var stepFactory = new TypeNameStepFactory();
        var host = new WorkflowEngineHost(provider, new JsonMessageSerializer(), stepFactory,
            new EngineOptions { FrameworkConfigLocation = tmp.Path, TierRebalanceIntervalSeconds = 5 });

        using var processor = host.CreateProcessor();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var task = Task.Run(() => processor.StartFrameworkAsync(Job, cts.Token));

        // Wait briefly for SelectBestTierAsync to run.
        await Task.Delay(500);

        await processor.StopFrameworkAsync(isSoftExit: true);
        try { await task; } catch { }

        // Reverse iteration: tier 1 probed first, then tier 0 if tier 1 had no messages.
        // Tier 1 had messages, so the consumer should have bound to it.
        Assert.Contains(1, probedTiers);
        Assert.Equal(1, provider.SelectedConsumeTier);
    }

    // ----- helpers -----

    private sealed class FailingTier0Provider : IQueueProvider
    {
        public string Name => "TestTier0Failing";
        public int Tier0PublishAttempts { get; private set; }
        public int Tier1PublishAttempts { get; private set; }

        public ValueTask<QueueHealth> CheckHealthAsync(string jobName, int tier = 0, CancellationToken cancellationToken = default)
            => new(new QueueHealth(tier > 0, true, true, true, 0, $"tier {tier}"));

        public ValueTask PublishAsync(LogicalQueue destination, ReadOnlyMemory<byte> body, MessageHeaders headers, CancellationToken cancellationToken = default)
        {
            if (destination.Tier == 0)
            {
                Tier0PublishAttempts++;
                throw new QueueUnavailableException("Simulated primary outage");
            }
            Tier1PublishAttempts++;
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishBatchAsync(IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public IAsyncEnumerable<IReceivedMessage> ConsumeAsync(string jobName, ConsumeOptions options, int tier = 0, CancellationToken cancellationToken = default) => EmptyAsyncEnumerable();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<IReceivedMessage> EmptyAsyncEnumerable()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TierHealthProbingProvider : IQueueProvider
    {
        private readonly Dictionary<int, long> _mainDepthByTier;
        private readonly Action<int> _onCheckHealth;

        public TierHealthProbingProvider(Dictionary<int, long> mainDepthByTier, Action<int> onCheckHealth)
        {
            _mainDepthByTier = mainDepthByTier;
            _onCheckHealth = onCheckHealth;
        }

        public string Name => "TestTierProbing";
        public int SelectedConsumeTier { get; private set; } = -1;

        public ValueTask<QueueHealth> CheckHealthAsync(string jobName, int tier = 0, CancellationToken cancellationToken = default)
        {
            _onCheckHealth(tier);
            var depth = _mainDepthByTier.TryGetValue(tier, out var d) ? d : 0;
            return new(new QueueHealth(true, true, true, true, depth, $"tier {tier}"));
        }

        public ValueTask PublishAsync(LogicalQueue destination, ReadOnlyMemory<byte> body, MessageHeaders headers, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask PublishBatchAsync(IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<IReceivedMessage> ConsumeAsync(string jobName, ConsumeOptions options, int tier = 0, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SelectedConsumeTier = tier;
            // Block until cancellation — we only care that the processor BOUND to this tier.
            try { await Task.Delay(Timeout.Infinite, cancellationToken); } catch { }
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TwoTierWorkflowXml : IDisposable
    {
        public string Path { get; }
        public TwoTierWorkflowXml(string jobName)
        {
            Path = System.IO.Path.GetTempFileName();
            File.WriteAllText(Path,
                $"""
                <Workflow>
                    <Job Name="{jobName}" MessageClass="ExampleMessage" NotifyComplete="false" MaxRunTimeMilliseconds="60000" MessageQueueType="Transactional">
                        <Queues>
                            <Queue MessageQueue="{jobName}.Main" ErrorQueue="{jobName}.Error" PoisonQueue="{jobName}.Poison" CompletedQueue="{jobName}.Completed" MessageQueueType="Transactional" />
                            <Queue MessageQueue="{jobName}.Backup.Main" ErrorQueue="{jobName}.Backup.Error" PoisonQueue="{jobName}.Backup.Poison" CompletedQueue="{jobName}.Backup.Completed" MessageQueueType="Transactional" />
                        </Queues>
                        <Steps>
                            <Step Name="NoOp" Group="g" InvokeClass="System.String" OnError="Skip" RetryTimes="0" RunMode="Synchronous" />
                        </Steps>
                    </Job>
                </Workflow>
                """);
        }
        public void Dispose() { try { File.Delete(Path); } catch { } }
    }
}
