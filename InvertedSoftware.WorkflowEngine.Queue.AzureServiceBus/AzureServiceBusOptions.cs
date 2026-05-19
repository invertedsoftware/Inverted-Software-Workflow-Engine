// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Queue.AzureServiceBus;

/// <summary>Azure Service Bus provider configuration.</summary>
public sealed class AzureServiceBusOptions : QueueProviderOptions
{
    public override QueueProviderKind Kind => QueueProviderKind.AzureServiceBus;

    /// <summary>FQDN of the namespace, e.g. <c>wf-engine.servicebus.windows.net</c>.</summary>
    public string FullyQualifiedNamespace { get; set; } = string.Empty;

    /// <summary>Optional connection string. Mutually exclusive with <see cref="UseManagedIdentity"/>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Authenticate with Azure managed identity (default). Ignored when <see cref="ConnectionString"/> is set.</summary>
    public bool UseManagedIdentity { get; set; } = true;

    /// <summary>Per-(job, kind) physical mapping.</summary>
    public Dictionary<string, AsbDestination> Mappings { get; init; } = new(StringComparer.Ordinal);

    public int MaxConcurrentCalls { get; set; } = 15;
    public int PrefetchCount { get; set; } = 16;
    public TimeSpan MaxAutoLockRenewalDuration { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>Entity name + type (Queue or Topic).</summary>
public sealed record AsbDestination(string Entity, AsbEntityType EntityType = AsbEntityType.Queue);

public enum AsbEntityType { Queue, Topic }
