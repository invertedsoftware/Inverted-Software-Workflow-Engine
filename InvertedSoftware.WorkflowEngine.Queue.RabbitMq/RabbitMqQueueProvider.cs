// Copyright (c) Inverted Software. All rights reserved.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace InvertedSoftware.WorkflowEngine.Queue.RabbitMq;

/// <summary>
/// RabbitMQ <see cref="IQueueProvider"/> built on the official 7.x async client.
///
/// <para>Mapping:</para>
/// <list type="bullet">
///   <item>Publish: <c>BasicPublishAsync</c> on a confirm-enabled channel; awaits broker confirmation.</item>
///   <item>Atomic batch: <c>TxSelectAsync</c> / <c>TxCommitAsync</c> across both publishes.</item>
///   <item>Consume: <c>AsyncEventingBasicConsumer</c> bridged onto a bounded <see cref="Channel{T}"/>.</item>
///   <item>Ack/Nack: <c>BasicAckAsync</c> / <c>BasicNackAsync</c> on the delivery tag.</item>
///   <item>Failover: each entry in <see cref="RabbitMqOptions.ConnectionStrings"/> tried in turn on connect failures.</item>
/// </list>
/// </summary>
public sealed class RabbitMqQueueProvider : IQueueProvider
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqQueueProvider> _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<string, bool> _declaredJobs = new(StringComparer.Ordinal);

    private IConnection? _connection;
    private IChannel? _publishChannel;
    private bool _disposed;

    public RabbitMqQueueProvider(RabbitMqOptions options, ILogger<RabbitMqQueueProvider>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<RabbitMqQueueProvider>.Instance;
        if (_options.ConnectionStrings.Count == 0)
            throw new ArgumentException("At least one ConnectionString is required.", nameof(options));
    }

    public string Name => "RabbitMq";

    private RabbitMqDestination ResolveDestination(LogicalQueue queue)
    {
        if (!_options.Mappings.TryGetValue(queue.MappingKey, out var dest))
            throw new QueueProviderException(
                $"No RabbitMQ mapping configured for '{queue.MappingKey}'. " +
                $"Add 'WorkflowEngine:RabbitMq:Mappings:{queue.MappingKey}' to appsettings.");
        return dest;
    }

    private async ValueTask<IConnection> EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        // Fast path: both connection AND publish channel still healthy. A channel can
        // die from an AMQP-level error (e.g. publish to a missing exchange) while the
        // connection stays open, so checking only IsOpen on the connection is not enough.
        if (_connection?.IsOpen == true && _publishChannel?.IsOpen == true)
            return _connection;

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-checked after lock.
            if (_connection?.IsOpen == true && _publishChannel?.IsOpen == true)
                return _connection;

            // Connection is fine but channel died — just recreate the channel.
            if (_connection?.IsOpen == true && _publishChannel?.IsOpen != true)
            {
                if (_publishChannel is not null)
                {
                    try { await _publishChannel.DisposeAsync().ConfigureAwait(false); } catch { }
                }
                _publishChannel = await _connection.CreateChannelAsync(
                    new CreateChannelOptions(
                        publisherConfirmationsEnabled: _options.PublisherConfirms,
                        publisherConfirmationTrackingEnabled: _options.PublisherConfirms),
                    cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("RabbitMQ publish channel recreated on existing connection.");
                return _connection;
            }

            // Connection is dead (or never opened). Iterate failover candidates.
            if (_publishChannel is not null)
            {
                try { await _publishChannel.DisposeAsync().ConfigureAwait(false); } catch { }
                _publishChannel = null;
            }
            if (_connection is not null)
            {
                try { await _connection.DisposeAsync().ConfigureAwait(false); } catch { }
                _connection = null;
            }

            Exception? lastError = null;
            foreach (var conn in _options.ConnectionStrings)
            {
                try
                {
                    var factory = new ConnectionFactory { Uri = new Uri(conn) };
                    _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
                    _publishChannel = await _connection.CreateChannelAsync(
                        new CreateChannelOptions(
                            publisherConfirmationsEnabled: _options.PublisherConfirms,
                            publisherConfirmationTrackingEnabled: _options.PublisherConfirms),
                        cancellationToken).ConfigureAwait(false);

                    _logger.LogInformation("RabbitMQ connected to {Uri}.", new Uri(conn).Host);
                    return _connection;
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "RabbitMQ connection to {Conn} failed; trying next.", conn);
                    lastError = e;
                }
            }
            throw new QueueUnavailableException(
                $"All {_options.ConnectionStrings.Count} RabbitMQ connection candidates failed.", lastError!);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task DeclareTopologyIfNeededAsync(string jobName, int tier, CancellationToken cancellationToken)
    {
        var topologyKey = tier == 0 ? jobName : $"{jobName}#{tier}";
        if (!_options.DeclareTopologyOnStartup) return;
        if (!_declaredJobs.TryAdd(topologyKey, true)) return;

        var conn = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var ch = await conn.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (var kind in Enum.GetValues<LogicalQueueKind>())
        {
            var key = new LogicalQueue(jobName, kind, tier).MappingKey;
            if (!_options.Mappings.TryGetValue(key, out var dest)) continue;

            if (!string.IsNullOrEmpty(dest.Exchange))
                await ch.ExchangeDeclareAsync(dest.Exchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            await ch.QueueDeclareAsync(dest.Queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(dest.Exchange))
                await ch.QueueBindAsync(dest.Queue, dest.Exchange, dest.RoutingKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<QueueHealth> CheckHealthAsync(string jobName, int tier = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            var conn = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);

            // RabbitMQ closes the channel when QueueDeclarePassive hits a 404. Use a
            // FRESH channel per probe so one missing queue doesn't poison the others.
            async Task<(bool ok, long depth)> ProbeAsync(LogicalQueueKind kind)
            {
                if (!_options.Mappings.TryGetValue(new LogicalQueue(jobName, kind, tier).MappingKey, out var dest))
                    return (false, 0);
                try
                {
                    await using var probeCh = await conn.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    var info = await probeCh.QueueDeclarePassiveAsync(dest.Queue, cancellationToken).ConfigureAwait(false);
                    return (true, info.MessageCount);
                }
                catch (OperationInterruptedException)
                {
                    return (false, 0);
                }
            }

            var main = await ProbeAsync(LogicalQueueKind.Main).ConfigureAwait(false);
            var error = await ProbeAsync(LogicalQueueKind.Error).ConfigureAwait(false);
            var poison = await ProbeAsync(LogicalQueueKind.Poison).ConfigureAwait(false);
            var completed = await ProbeAsync(LogicalQueueKind.Completed).ConfigureAwait(false);

            return new QueueHealth(main.ok, error.ok, poison.ok, completed.ok, main.depth, _connection?.Endpoint.HostName);
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
        var conn = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
        await DeclareTopologyIfNeededAsync(destination.JobName, destination.Tier, cancellationToken).ConfigureAwait(false);

        var props = BuildBasicProperties(headers);

        try
        {
            await _publishChannel!.BasicPublishAsync(
                exchange: dest.Exchange,
                routingKey: dest.RoutingKey,
                mandatory: false,
                basicProperties: props,
                body: body,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is BrokerUnreachableException or AlreadyClosedException)
        {
            throw new QueueUnavailableException("RabbitMQ unreachable while publishing.", e);
        }
    }

    public async ValueTask PublishBatchAsync(
        IReadOnlyList<OutgoingMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (messages is null) throw new ArgumentNullException(nameof(messages));
        if (messages.Count == 0) return;

        var conn = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Use a dedicated tx channel so we don't pollute the long-lived publish channel.
        await using var ch = await conn.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await ch.TxSelectAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var m in messages)
            {
                await DeclareTopologyIfNeededAsync(m.Destination.JobName, m.Destination.Tier, cancellationToken).ConfigureAwait(false);
                var dest = ResolveDestination(m.Destination);
                var props = BuildBasicProperties(m.Headers);

                await ch.BasicPublishAsync(
                    exchange: dest.Exchange,
                    routingKey: dest.RoutingKey,
                    mandatory: false,
                    basicProperties: props,
                    body: m.Body,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            await ch.TxCommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { await ch.TxRollbackAsync(cancellationToken).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    public async IAsyncEnumerable<IReceivedMessage> ConsumeAsync(
        string jobName,
        ConsumeOptions options,
        int tier = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var mainDest = ResolveDestination(new LogicalQueue(jobName, LogicalQueueKind.Main, tier));
        var conn = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
        await DeclareTopologyIfNeededAsync(jobName, tier, cancellationToken).ConfigureAwait(false);

        var ch = await conn.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            await ch.BasicQosAsync(0, _options.Prefetch, global: false, cancellationToken).ConfigureAwait(false);

            var queue = Channel.CreateBounded<RabbitMqReceivedMessage>(
                new BoundedChannelOptions(_options.Prefetch * 2) { FullMode = BoundedChannelFullMode.Wait });

            var consumer = new AsyncEventingBasicConsumer(ch);
            consumer.ReceivedAsync += async (sender, ea) =>
            {
                var headers = MapHeaders(ea.BasicProperties, ea.Exchange);
                var msg = new RabbitMqReceivedMessage(ch, new LogicalQueue(jobName, LogicalQueueKind.Main, tier), ea.Body, headers, ea.DeliveryTag, autoAck: options.AutoAck);
                try
                {
                    await queue.Writer.WriteAsync(msg, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The iterator was cancelled between broker delivery and our channel write.
                    // For autoAck=false the broker will redeliver after this channel closes.
                    // For autoAck=true the broker has already acked — the message is lost; nack
                    // wouldn't help (no delivery tag scope left). Log so the count is visible.
                    _logger.LogDebug("Dropping in-flight delivery (tag={Tag}) during consumer cancellation.", ea.DeliveryTag);
                }
                catch (ChannelClosedException)
                {
                    // The iterator's finally already disposed the bridge channel; same handling.
                    _logger.LogDebug("Dropping in-flight delivery (tag={Tag}) — bridge channel closed.", ea.DeliveryTag);
                }
            };

            await ch.BasicConsumeAsync(
                mainDest.Queue,
                autoAck: options.AutoAck,
                consumer: consumer,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await foreach (var msg in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return msg;
            }
        }
        finally
        {
            try { await ch.CloseAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            await ch.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_publishChannel is not null) await _publishChannel.DisposeAsync().ConfigureAwait(false); } catch { }
        try { if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false); } catch { }
        _connectLock.Dispose();
    }

    private static BasicProperties BuildBasicProperties(MessageHeaders headers)
    {
        var props = new BasicProperties
        {
            ContentType = headers.ContentType,
            MessageId = headers.MessageId,
            CorrelationId = headers.CorrelationId,
            Persistent = true,
            Headers = new Dictionary<string, object?>(StringComparer.Ordinal),
        };
        foreach (var kv in headers)
            props.Headers[kv.Key] = Encoding.UTF8.GetBytes(kv.Value);
        if (!string.IsNullOrEmpty(headers.MessageType))
            props.Headers[MessageHeaders.MessageTypeHeader] = Encoding.UTF8.GetBytes(headers.MessageType);
        return props;
    }

    private static MessageHeaders MapHeaders(IReadOnlyBasicProperties props, string sourceExchange)
    {
        var h = new MessageHeaders
        {
            MessageId = props.IsMessageIdPresent() ? props.MessageId : null,
            CorrelationId = props.IsCorrelationIdPresent() ? props.CorrelationId : null,
            ContentType = props.IsContentTypePresent() ? props.ContentType : null,
            EnqueuedAtUtc = props.IsTimestampPresent() ? DateTimeOffset.FromUnixTimeSeconds(props.Timestamp.UnixTime) : DateTimeOffset.UtcNow,
        };
        h[MessageHeaders.SourcePhysicalHeader] = sourceExchange;
        if (props.IsHeadersPresent() && props.Headers is not null)
        {
            foreach (var kv in props.Headers)
            {
                if (kv.Value is byte[] bytes)
                {
                    var s = Encoding.UTF8.GetString(bytes);
                    if (kv.Key == MessageHeaders.MessageTypeHeader) h.MessageType = s;
                    else h[kv.Key] = s;
                }
            }
        }
        return h;
    }
}

internal sealed class RabbitMqReceivedMessage : IReceivedMessage
{
    private readonly IChannel _channel;
    private readonly bool _autoAck;
    private int _resolved;

    public RabbitMqReceivedMessage(IChannel channel, LogicalQueue source, ReadOnlyMemory<byte> body, MessageHeaders headers, ulong deliveryTag, bool autoAck)
    {
        _channel = channel;
        _autoAck = autoAck;
        Source = source;
        Body = body;
        Headers = headers;
        DeliveryTag = deliveryTag;
    }

    public ReadOnlyMemory<byte> Body { get; }
    public MessageHeaders Headers { get; }
    public LogicalQueue Source { get; }
    public object DeliveryTag { get; }

    public async ValueTask AckAsync(CancellationToken cancellationToken = default)
    {
        // When autoAck is enabled on the channel, the broker has already removed the
        // message and the delivery tag is invalid — calling BasicAckAsync would close
        // the channel with PRECONDITION_FAILED.
        if (_autoAck) return;
        if (Interlocked.Exchange(ref _resolved, 1) == 1) return;
        await _channel.BasicAckAsync((ulong)DeliveryTag, multiple: false, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask NackAsync(bool requeue, CancellationToken cancellationToken = default)
    {
        if (_autoAck) return;
        if (Interlocked.Exchange(ref _resolved, 1) == 1) return;
        await _channel.BasicNackAsync((ulong)DeliveryTag, multiple: false, requeue: requeue, cancellationToken).ConfigureAwait(false);
    }

    public object DeserializeBody(IMessageSerializer serializer)
    {
        var typeName = Headers.MessageType
            ?? throw new MessageDeserializationException($"No '{MessageHeaders.MessageTypeHeader}' header on message from {Source}.");
        return serializer.Deserialize(Body, TypeNameResolver.ResolveOrThrow(typeName));
    }

    public T DeserializeBody<T>(IMessageSerializer serializer) => serializer.Deserialize<T>(Body);
}
