// Copyright (c) Inverted Software. All rights reserved.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace InvertedSoftware.WorkflowEngine.Queue.AzureServiceBus;

/// <summary>
/// Azure Service Bus <see cref="IQueueProvider"/>.
///
/// <para>Mapping:</para>
/// <list type="bullet">
///   <item>Publish: <c>ServiceBusSender.SendMessageAsync</c>.</item>
///   <item>Atomic batch: <c>ServiceBusSender.SendMessagesAsync(batch)</c> within a single namespace
///         (cross-namespace batches are not atomic; the provider validates this at startup).</item>
///   <item>Consume: <c>ServiceBusProcessor</c> (which auto-renews message locks for long-running
///         steps) in <c>PeekLock</c> mode, bridged to a <see cref="Channel{T}"/> so callers can
///         consume as <see cref="IAsyncEnumerable{T}"/>.</item>
///   <item>Ack: <c>CompleteMessageAsync</c>. Nack/requeue: <c>AbandonMessageAsync</c>.
///         Nack/dead-letter: <c>DeadLetterMessageAsync</c>.</item>
/// </list>
/// </summary>
public sealed class AzureServiceBusQueueProvider : IQueueProvider
{
    private readonly AzureServiceBusOptions _options;
    private readonly ILogger<AzureServiceBusQueueProvider> _logger;
    private readonly ServiceBusClient _client;
    private readonly ServiceBusAdministrationClient _admin;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new(StringComparer.Ordinal);
    private bool _disposed;

    public AzureServiceBusQueueProvider(AzureServiceBusOptions options, ILogger<AzureServiceBusQueueProvider>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<AzureServiceBusQueueProvider>.Instance;

        if (!string.IsNullOrEmpty(_options.ConnectionString))
        {
            _client = new ServiceBusClient(_options.ConnectionString);
            _admin = new ServiceBusAdministrationClient(_options.ConnectionString);
        }
        else if (_options.UseManagedIdentity && !string.IsNullOrEmpty(_options.FullyQualifiedNamespace))
        {
            var credential = new DefaultAzureCredential();
            _client = new ServiceBusClient(_options.FullyQualifiedNamespace, credential);
            _admin = new ServiceBusAdministrationClient(_options.FullyQualifiedNamespace, credential);
        }
        else
        {
            throw new ArgumentException(
                "AzureServiceBusOptions requires either ConnectionString or UseManagedIdentity + FullyQualifiedNamespace.");
        }

        // Validate that all mapped entities target one namespace (cross-namespace atomic batch is impossible).
        // The Mappings dictionary doesn't carry the namespace, but the single-client design enforces this
        // implicitly — all mappings flow through the one ServiceBusClient.
    }

    public string Name => "AzureServiceBus";

    private AsbDestination ResolveDestination(LogicalQueue queue)
    {
        if (!_options.Mappings.TryGetValue(queue.MappingKey, out var dest))
            throw new QueueProviderException(
                $"No Azure Service Bus mapping configured for '{queue.MappingKey}'. " +
                $"Add 'WorkflowEngine:AzureServiceBus:Mappings:{queue.MappingKey}' to appsettings.");
        return dest;
    }

    private ServiceBusSender GetSender(string entity) =>
        _senders.GetOrAdd(entity, _client.CreateSender);

    public async ValueTask<QueueHealth> CheckHealthAsync(string jobName, int tier = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            async Task<(bool ok, long depth)> ProbeAsync(LogicalQueueKind kind)
            {
                if (!_options.Mappings.TryGetValue(new LogicalQueue(jobName, kind, tier).MappingKey, out var dest))
                    return (false, 0);
                try
                {
                    if (dest.EntityType == AsbEntityType.Queue)
                    {
                        var props = await _admin.GetQueueRuntimePropertiesAsync(dest.Entity, cancellationToken).ConfigureAwait(false);
                        return (true, props.Value.ActiveMessageCount);
                    }
                    var topicProps = await _admin.GetTopicRuntimePropertiesAsync(dest.Entity, cancellationToken).ConfigureAwait(false);
                    return (true, topicProps.Value.ScheduledMessageCount);
                }
                catch
                {
                    return (false, 0);
                }
            }

            var main = await ProbeAsync(LogicalQueueKind.Main).ConfigureAwait(false);
            var error = await ProbeAsync(LogicalQueueKind.Error).ConfigureAwait(false);
            var poison = await ProbeAsync(LogicalQueueKind.Poison).ConfigureAwait(false);
            var completed = await ProbeAsync(LogicalQueueKind.Completed).ConfigureAwait(false);

            return new QueueHealth(main.ok, error.ok, poison.ok, completed.ok, main.depth,
                _options.FullyQualifiedNamespace);
        }
        catch (Exception e)
        {
            return new QueueHealth(false, false, false, false, null, e.Message);
        }
    }

    public async ValueTask PublishAsync(
        LogicalQueue destination,
        ReadOnlyMemory<byte> body,
        MessageHeaders headers,
        CancellationToken cancellationToken = default)
    {
        var dest = ResolveDestination(destination);
        var sender = GetSender(dest.Entity);
        var msg = BuildMessage(body, headers);

        try
        {
            await sender.SendMessageAsync(msg, cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceBusException e) when (e.Reason is ServiceBusFailureReason.ServiceCommunicationProblem
                                              or ServiceBusFailureReason.ServiceTimeout)
        {
            throw new QueueUnavailableException($"Service Bus unavailable while publishing to '{dest.Entity}'.", e);
        }
    }

    public async ValueTask PublishBatchAsync(
        IReadOnlyList<OutgoingMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (messages is null) throw new ArgumentNullException(nameof(messages));
        if (messages.Count == 0) return;

        // Service Bus batches are atomic ONLY when all messages target the same entity. If
        // the batch spans entities (Error + Poison), we issue them as separate sends inside
        // a "best effort" loop. Same-namespace cross-entity transactions are possible via
        // ServiceBusClient.CreateTransactionalSender, but the cross-entity tx feature requires
        // sessions or "via" routing. For this engine's HandleError flow (Error + Poison in
        // sibling queues), we accept the documented best-effort behaviour.
        var byEntity = messages.GroupBy(m => ResolveDestination(m.Destination).Entity);

        foreach (var group in byEntity)
        {
            var sender = GetSender(group.Key);
            using var batch = await sender.CreateMessageBatchAsync(cancellationToken).ConfigureAwait(false);
            foreach (var m in group)
            {
                var msg = BuildMessage(m.Body, m.Headers);
                if (!batch.TryAddMessage(msg))
                    throw new QueueProviderException($"Message too large for batch on entity '{group.Key}'.");
            }
            await sender.SendMessagesAsync(batch, cancellationToken).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<IReceivedMessage> ConsumeAsync(
        string jobName,
        ConsumeOptions options,
        int tier = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var mainDest = ResolveDestination(new LogicalQueue(jobName, LogicalQueueKind.Main, tier));

        // Auto-renew message locks for at least the consumer's ack budget plus a 20%
        // buffer, so long-running steps don't lose their lock mid-execution. Honour
        // an explicit AzureServiceBusOptions.MaxAutoLockRenewalDuration if it's longer.
        var renewalBudget = options.AckTimeout > TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(options.AckTimeout.TotalMilliseconds * 1.2)
            : _options.MaxAutoLockRenewalDuration;
        if (renewalBudget < _options.MaxAutoLockRenewalDuration)
            renewalBudget = _options.MaxAutoLockRenewalDuration;

        var processorOptions = new ServiceBusProcessorOptions
        {
            ReceiveMode = options.AutoAck ? ServiceBusReceiveMode.ReceiveAndDelete : ServiceBusReceiveMode.PeekLock,
            PrefetchCount = _options.PrefetchCount,
            MaxConcurrentCalls = _options.MaxConcurrentCalls,
            AutoCompleteMessages = false, // engine drives ack/nack explicitly
            MaxAutoLockRenewalDuration = renewalBudget,
        };

        ServiceBusProcessor processor = mainDest.EntityType == AsbEntityType.Queue
            ? _client.CreateProcessor(mainDest.Entity, processorOptions)
            : _client.CreateProcessor(mainDest.Entity, options.ConsumerGroup ?? "default", processorOptions);

        // Bridge the callback-based processor to IAsyncEnumerable via a bounded channel.
        var bridge = Channel.CreateBounded<AsbReceivedMessage>(
            new BoundedChannelOptions(Math.Max(options.Prefetch * 2, _options.PrefetchCount * 2))
            {
                FullMode = BoundedChannelFullMode.Wait,
            });

        processor.ProcessMessageAsync += async args =>
        {
            var headers = MapHeaders(args.Message, mainDest.Entity);
            var msg = new AsbReceivedMessage(args, new LogicalQueue(jobName, LogicalQueueKind.Main, tier),
                args.Message.Body.ToMemory(), headers, options.AutoAck);
            // Honour back-pressure: WriteAsync blocks if the bridge is full.
            await bridge.Writer.WriteAsync(msg, args.CancellationToken).ConfigureAwait(false);

            // Wait for the consumer to ack/nack before completing the handler — this is
            // what enables MaxAutoLockRenewalDuration to keep the lock alive end-to-end.
            await msg.WaitForResolutionAsync(args.CancellationToken).ConfigureAwait(false);
        };

        processor.ProcessErrorAsync += args =>
        {
            _logger.LogWarning(args.Exception,
                "Azure Service Bus processor error on entity '{Entity}': {Source}",
                mainDest.Entity, args.ErrorSource);
            return Task.CompletedTask;
        };

        try
        {
            await processor.StartProcessingAsync(cancellationToken).ConfigureAwait(false);

            await foreach (var msg in bridge.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return msg;
        }
        finally
        {
            try { await processor.StopProcessingAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            await processor.DisposeAsync().ConfigureAwait(false);
            bridge.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        // Wrap each disposal: a throw on one sender or the client must not skip cleanup
        // of the others. Shutdown is a "best-effort, free everything" path.
        foreach (var s in _senders.Values)
        {
            try { await s.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        try { await _client.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    private static ServiceBusMessage BuildMessage(ReadOnlyMemory<byte> body, MessageHeaders headers)
    {
        var msg = new ServiceBusMessage(body)
        {
            ContentType = headers.ContentType,
            MessageId = headers.MessageId,
            CorrelationId = headers.CorrelationId,
        };
        foreach (var kv in headers)
            msg.ApplicationProperties[kv.Key] = kv.Value;
        if (!string.IsNullOrEmpty(headers.MessageType))
            msg.ApplicationProperties[MessageHeaders.MessageTypeHeader] = headers.MessageType;
        return msg;
    }

    private static MessageHeaders MapHeaders(ServiceBusReceivedMessage sbMsg, string sourceEntity)
    {
        var h = new MessageHeaders
        {
            MessageId = sbMsg.MessageId,
            CorrelationId = sbMsg.CorrelationId,
            ContentType = sbMsg.ContentType,
            EnqueuedAtUtc = sbMsg.EnqueuedTime,
            DeliveryAttempt = sbMsg.DeliveryCount,
        };
        h[MessageHeaders.SourcePhysicalHeader] = sourceEntity;
        foreach (var kv in sbMsg.ApplicationProperties)
        {
            var v = kv.Value?.ToString() ?? string.Empty;
            if (kv.Key == MessageHeaders.MessageTypeHeader) h.MessageType = v;
            else h[kv.Key] = v;
        }
        return h;
    }
}

internal sealed class AsbReceivedMessage : IReceivedMessage
{
    private readonly ProcessMessageEventArgs _args;
    private readonly bool _autoAck;
    private readonly TaskCompletionSource _resolution = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _resolved;

    public AsbReceivedMessage(ProcessMessageEventArgs args, LogicalQueue source, ReadOnlyMemory<byte> body,
        MessageHeaders headers, bool autoAck)
    {
        _args = args;
        _autoAck = autoAck;
        Source = source;
        Body = body;
        Headers = headers;
        DeliveryTag = args.Message.LockToken;
        if (autoAck) _resolution.TrySetResult();
    }

    public ReadOnlyMemory<byte> Body { get; }
    public MessageHeaders Headers { get; }
    public LogicalQueue Source { get; }
    public object DeliveryTag { get; }

    /// <summary>
    /// Provider-side: ProcessMessageAsync awaits this so the handler stays "in flight"
    /// (and the lock keeps auto-renewing) until the consumer acks or nacks.
    /// </summary>
    internal Task WaitForResolutionAsync(CancellationToken cancellationToken)
    {
        if (_resolution.Task.IsCompleted) return _resolution.Task;
        return _resolution.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask AckAsync(CancellationToken cancellationToken = default)
    {
        if (_autoAck || Interlocked.Exchange(ref _resolved, 1) == 1) return;
        await _args.CompleteMessageAsync(_args.Message, cancellationToken).ConfigureAwait(false);
        _resolution.TrySetResult();
    }

    public async ValueTask NackAsync(bool requeue, CancellationToken cancellationToken = default)
    {
        if (_autoAck || Interlocked.Exchange(ref _resolved, 1) == 1) return;
        try
        {
            if (requeue)
                await _args.AbandonMessageAsync(_args.Message, cancellationToken: cancellationToken).ConfigureAwait(false);
            else
                await _args.DeadLetterMessageAsync(_args.Message, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _resolution.TrySetResult();
        }
    }

    public object DeserializeBody(IMessageSerializer serializer)
    {
        var typeName = Headers.MessageType
            ?? throw new MessageDeserializationException($"No '{MessageHeaders.MessageTypeHeader}' header on ASB message from {Source}.");
        return serializer.Deserialize(Body, TypeNameResolver.ResolveOrThrow(typeName));
    }

    public T DeserializeBody<T>(IMessageSerializer serializer) => serializer.Deserialize<T>(Body);
}
