// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Queue;

/// <summary>Provider discriminator.</summary>
public enum QueueProviderKind
{
    InMemory,
    RabbitMq,
    Kafka,
    AzureServiceBus,
}

/// <summary>
/// Common base for provider-specific options classes. Each provider extends this
/// and adds its own connection/auth/tuning fields.
/// </summary>
public abstract class QueueProviderOptions
{
    /// <summary>Discriminator used by the DI builder to pick a provider.</summary>
    public abstract QueueProviderKind Kind { get; }
}
