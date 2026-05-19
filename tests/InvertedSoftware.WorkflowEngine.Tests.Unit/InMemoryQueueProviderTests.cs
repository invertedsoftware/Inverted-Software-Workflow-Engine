// Copyright (c) Inverted Software. All rights reserved.

using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.Queue;
using InvertedSoftware.WorkflowEngine.Queue.InMemory;
using InvertedSoftware.WorkflowEngine.Queue.Serialization;
using Xunit;

namespace InvertedSoftware.WorkflowEngine.Tests.Unit;

/// <summary>
/// Sanity checks for the in-memory provider — these are also the contract every
/// real provider must satisfy.
/// </summary>
public class InMemoryQueueProviderTests
{
    private static readonly LogicalQueue Main = new("TestJob", LogicalQueueKind.Main);

    [Fact]
    public async Task Publish_Consume_Roundtrip_Preserves_Body_And_Headers()
    {
        await using var provider = new InMemoryQueueProvider();
        var serializer = new JsonMessageSerializer();

        var payload = new ExampleMessage { JobID = 42, CopyFilesFrom = "a", CopyFilesTo = "b" };
        var headers = new MessageHeaders
        {
            ContentType = serializer.ContentType,
            MessageType = typeof(ExampleMessage).FullName,
            CorrelationId = "42",
        };
        await provider.PublishAsync(Main, serializer.Serialize(payload), headers);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var received in provider.ConsumeAsync("TestJob", new ConsumeOptions { Prefetch = 1 }, cancellationToken: cts.Token))
        {
            var roundTripped = (ExampleMessage)received.DeserializeBody(serializer);
            Assert.Equal(42, roundTripped.JobID);
            Assert.Equal("a", roundTripped.CopyFilesFrom);
            Assert.Equal("b", roundTripped.CopyFilesTo);
            Assert.Equal("42", received.Headers.CorrelationId);
            await received.AckAsync();
            return;
        }
        Assert.Fail("No message received within 2s.");
    }

    [Fact]
    public async Task PublishBatch_Atomic_Delivers_All_Or_None()
    {
        await using var provider = new InMemoryQueueProvider();
        var serializer = new JsonMessageSerializer();

        var poison = new ExampleMessage { JobID = 7 };
        var error = new WorkflowErrorMessage { ExceptionMessage = "boom" };
        var headers = new MessageHeaders { ContentType = serializer.ContentType, MessageType = typeof(ExampleMessage).FullName };
        var errorHeaders = new MessageHeaders { ContentType = serializer.ContentType, MessageType = typeof(WorkflowErrorMessage).FullName };

        await provider.PublishBatchAsync(new[]
        {
            new OutgoingMessage(new LogicalQueue("TestJob", LogicalQueueKind.Poison), serializer.Serialize(poison), headers),
            new OutgoingMessage(new LogicalQueue("TestJob", LogicalQueueKind.Error),  serializer.Serialize(error),  errorHeaders),
        });

        var health = await provider.CheckHealthAsync("TestJob");
        Assert.True(health.MainAvailable);
    }
}
