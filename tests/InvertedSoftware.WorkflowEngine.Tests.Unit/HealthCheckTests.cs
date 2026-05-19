// Copyright (c) Inverted Software. All rights reserved.

using InvertedSoftware.WorkflowEngine.Config;
using InvertedSoftware.WorkflowEngine.Hosting;
using InvertedSoftware.WorkflowEngine.Queue.InMemory;
using InvertedSoftware.WorkflowEngine.Queue.Serialization;
using InvertedSoftware.WorkflowEngine.Steps;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace InvertedSoftware.WorkflowEngine.Tests.Unit;

public class HealthCheckTests
{
    private const string Job = "HealthJob";

    [Fact]
    public async Task HealthCheck_Reports_Healthy_When_Provider_Is_Available()
    {
        using var tmp = new TestWorkflowXml(Job, ("S", "Step"));
        await using var queue = new InMemoryQueueProvider();
        var host = new WorkflowEngineHost(queue, new JsonMessageSerializer(),
            new TypeNameStepFactory(), new EngineOptions { FrameworkConfigLocation = tmp.Path });

        var check = new WorkflowQueueHealthCheck(host, Job);
        var result = await check.CheckHealthAsync(new HealthCheckContext { Registration = new HealthCheckRegistration("wf", check, null, null) });

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("InMemory", result.Data["provider"]);
        Assert.True((bool)result.Data["main_available"]);
    }

    [Fact]
    public async Task HealthCheck_Reports_Unhealthy_On_Provider_Error()
    {
        var failingProvider = new FailingQueueProvider();
        var host = new WorkflowEngineHost(failingProvider, new JsonMessageSerializer(),
            new TypeNameStepFactory(), new EngineOptions());

        var check = new WorkflowQueueHealthCheck(host, Job);
        var result = await check.CheckHealthAsync(new HealthCheckContext { Registration = new HealthCheckRegistration("wf", check, null, null) });

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private sealed class FailingQueueProvider : Queue.IQueueProvider
    {
        public string Name => "Failing";
        public ValueTask<Queue.QueueHealth> CheckHealthAsync(string jobName, CancellationToken cancellationToken = default)
            => new(new Queue.QueueHealth(false, false, false, false, null, "broker offline"));
        public ValueTask PublishAsync(Queue.LogicalQueue destination, ReadOnlyMemory<byte> body, Queue.MessageHeaders headers, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask PublishBatchAsync(IReadOnlyList<Queue.OutgoingMessage> messages, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IAsyncEnumerable<Queue.IReceivedMessage> ConsumeAsync(string jobName, Queue.ConsumeOptions options, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
