// Copyright (c) Inverted Software. All rights reserved.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace InvertedSoftware.WorkflowEngine.Queue.Kafka;

/// <summary>
/// Apache Kafka <see cref="IQueueProvider"/> built on Confluent.Kafka 2.x.
///
/// <para>Mapping:</para>
/// <list type="bullet">
///   <item>Publish: <c>ProduceAsync</c> with <c>acks=all</c> + idempotence (default).</item>
///   <item>Atomic batch: transactional producer (requires <see cref="KafkaOptions.TransactionalId"/>).</item>
///   <item>Consume: <c>Consume(timeout)</c> on a background task, bridged to a <see cref="Channel{T}"/>.</item>
///   <item>Ack: advance the in-order committed-offset watermark per partition. A nack-with-requeue stops
///         the partition (Kafka cannot re-insert a message before its offset).</item>
///   <item>Ordering: messages keyed by <c>CorrelationId</c> (= JobID) land on the same partition.</item>
/// </list>
/// </summary>
public sealed class KafkaQueueProvider : IQueueProvider
{
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaQueueProvider> _logger;
    private readonly IProducer<string?, byte[]> _producer;
    private readonly Lazy<IAdminClient> _admin;
    private readonly object _txLock = new();
    private bool _txInitialised;
    private bool _disposed;

    public KafkaQueueProvider(KafkaOptions options, ILogger<KafkaQueueProvider>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<KafkaQueueProvider>.Instance;

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            Acks = ParseAcks(_options.Acks),
            EnableIdempotence = _options.EnableIdempotence,
            TransactionalId = _options.TransactionalId,
            SecurityProtocol = ParseSecurity(_options.SecurityProtocol),
            SaslMechanism = _options.SaslMechanism is null ? null : Enum.Parse<SaslMechanism>(_options.SaslMechanism, ignoreCase: true),
            SaslUsername = _options.SaslUsername,
            SaslPassword = _options.SaslPassword,
        };
        _producer = new ProducerBuilder<string?, byte[]>(producerConfig).Build();

        _admin = new Lazy<IAdminClient>(() => new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = _options.BootstrapServers,
            SecurityProtocol = producerConfig.SecurityProtocol,
            SaslMechanism = producerConfig.SaslMechanism,
            SaslUsername = producerConfig.SaslUsername,
            SaslPassword = producerConfig.SaslPassword,
        }).Build());
    }

    public string Name => "Kafka";

    private KafkaDestination ResolveDestination(LogicalQueue queue)
    {
        if (!_options.Mappings.TryGetValue(queue.MappingKey, out var dest))
            throw new QueueProviderException(
                $"No Kafka mapping configured for '{queue.MappingKey}'. " +
                $"Add 'WorkflowEngine:Kafka:Mappings:{queue.MappingKey}' to appsettings.");
        return dest;
    }

    public ValueTask<QueueHealth> CheckHealthAsync(string jobName, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = _admin.Value.GetMetadata(TimeSpan.FromSeconds(5));
            bool Probe(LogicalQueueKind kind)
            {
                if (!_options.Mappings.TryGetValue(new LogicalQueue(jobName, kind).MappingKey, out var dest))
                    return false;
                return metadata.Topics.Any(t => t.Topic == dest.Topic && t.Error.Code == ErrorCode.NoError);
            }
            return new ValueTask<QueueHealth>(new QueueHealth(
                Probe(LogicalQueueKind.Main),
                Probe(LogicalQueueKind.Error),
                Probe(LogicalQueueKind.Poison),
                Probe(LogicalQueueKind.Completed),
                ApproximateMainDepth: null, // Kafka cannot report this cheaply without a consumer group assignment.
                Diagnostic: $"Kafka cluster: {metadata.OriginatingBrokerName}"));
        }
        catch (Exception e)
        {
            return new ValueTask<QueueHealth>(new QueueHealth(false, false, false, false, null, e.Message));
        }
    }

    public async ValueTask PublishAsync(
        LogicalQueue destination,
        ReadOnlyMemory<byte> body,
        MessageHeaders headers,
        CancellationToken cancellationToken = default)
    {
        var dest = ResolveDestination(destination);
        var message = BuildMessage(body, headers);

        try
        {
            await _producer.ProduceAsync(dest.Topic, message, cancellationToken).ConfigureAwait(false);
        }
        catch (ProduceException<string?, byte[]> e)
        {
            throw new QueueUnavailableException($"Kafka produce to '{dest.Topic}' failed: {e.Error.Reason}", e);
        }
    }

    public async ValueTask PublishBatchAsync(
        IReadOnlyList<OutgoingMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (messages is null) throw new ArgumentNullException(nameof(messages));
        if (messages.Count == 0) return;

        if (string.IsNullOrEmpty(_options.TransactionalId))
        {
            // Best-effort sequential publish without atomicity.
            foreach (var m in messages)
                await PublishAsync(m.Destination, m.Body, m.Headers, cancellationToken).ConfigureAwait(false);
            return;
        }

        EnsureTxInitialised();

        try
        {
            _producer.BeginTransaction();
            foreach (var m in messages)
            {
                var dest = ResolveDestination(m.Destination);
                _producer.Produce(dest.Topic, BuildMessage(m.Body, m.Headers));
            }
            _producer.CommitTransaction();
        }
        catch
        {
            try { _producer.AbortTransaction(); } catch { /* nested failure */ }
            throw;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async IAsyncEnumerable<IReceivedMessage> ConsumeAsync(
        string jobName,
        ConsumeOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var mainDest = ResolveDestination(new LogicalQueue(jobName, LogicalQueueKind.Main));
        var consumerGroup = options.ConsumerGroup ?? mainDest.ConsumerGroup
            ?? throw new QueueProviderException(
                $"Kafka Main destination for job '{jobName}' must specify a ConsumerGroup " +
                "(in options.Mappings or via ConsumeOptions.ConsumerGroup).");

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = consumerGroup,
            EnableAutoCommit = false,
            AutoOffsetReset = Enum.Parse<AutoOffsetReset>(_options.AutoOffsetReset, ignoreCase: true),
            SecurityProtocol = ParseSecurity(_options.SecurityProtocol),
            SaslMechanism = _options.SaslMechanism is null ? null : Enum.Parse<SaslMechanism>(_options.SaslMechanism, ignoreCase: true),
            SaslUsername = _options.SaslUsername,
            SaslPassword = _options.SaslPassword,
        };
        using var consumer = new ConsumerBuilder<string?, byte[]>(consumerConfig).Build();
        consumer.Subscribe(mainDest.Topic);

        var channel = Channel.CreateBounded<KafkaReceivedMessage>(
            new BoundedChannelOptions(Math.Max(options.Prefetch * 2, 16)) { FullMode = BoundedChannelFullMode.Wait });

        // In-order offset tracker: tracks acked offsets per (topic, partition); only
        // commit up to the highest contiguous offset.
        var tracker = new OffsetWatermark(consumer);

        var pollTask = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    ConsumeResult<string?, byte[]>? result;
                    try { result = consumer.Consume(_options.ConsumeTimeout); }
                    catch (ConsumeException e)
                    {
                        _logger.LogWarning(e, "Kafka consume error: {Reason}", e.Error.Reason);
                        continue;
                    }
                    if (result?.Message is null) continue;

                    var tpo = new TopicPartitionOffset(result.Topic, result.Partition, result.Offset);
                    // Anchor the per-partition watermark to the first delivered offset so
                    // committed positions are valid (otherwise StoreOffset stays at -2 forever).
                    tracker.RecordDelivered(tpo);

                    var headers = MapHeaders(result.Message.Headers, result.Topic);
                    var msg = new KafkaReceivedMessage(
                        tracker,
                        tpo,
                        new LogicalQueue(jobName, LogicalQueueKind.Main),
                        result.Message.Value,
                        headers,
                        options.AutoAck);

                    await channel.Writer.WriteAsync(msg, cancellationToken).ConfigureAwait(false);
                    if (options.AutoAck)
                        msg.MarkAuto();
                }
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        try
        {
            await foreach (var msg in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return msg;
        }
        finally
        {
            consumer.Close();
            try { await pollTask.ConfigureAwait(false); } catch { }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        try { _producer.Flush(TimeSpan.FromSeconds(5)); } catch { }
        _producer.Dispose();
        if (_admin.IsValueCreated) _admin.Value.Dispose();
        return ValueTask.CompletedTask;
    }

    private void EnsureTxInitialised()
    {
        if (_txInitialised) return;
        lock (_txLock)
        {
            if (_txInitialised) return;
            _producer.InitTransactions(TimeSpan.FromSeconds(30));
            _txInitialised = true;
        }
    }

    private static Message<string?, byte[]> BuildMessage(ReadOnlyMemory<byte> body, MessageHeaders headers)
    {
        var kafkaHeaders = new Headers();
        foreach (var kv in headers)
            kafkaHeaders.Add(kv.Key, Encoding.UTF8.GetBytes(kv.Value));
        if (!string.IsNullOrEmpty(headers.MessageType))
            kafkaHeaders.Add(MessageHeaders.MessageTypeHeader, Encoding.UTF8.GetBytes(headers.MessageType));
        return new Message<string?, byte[]>
        {
            // CorrelationId == JobID — partitioning by this preserves per-job ordering.
            Key = headers.CorrelationId,
            Value = body.ToArray(),
            Headers = kafkaHeaders,
            Timestamp = headers.EnqueuedAtUtc.HasValue
                ? new Timestamp(headers.EnqueuedAtUtc.Value.UtcDateTime, TimestampType.CreateTime)
                : new Timestamp(DateTime.UtcNow, TimestampType.CreateTime),
        };
    }

    private static MessageHeaders MapHeaders(Headers? kafkaHeaders, string sourceTopic)
    {
        var h = new MessageHeaders { EnqueuedAtUtc = DateTimeOffset.UtcNow };
        h[MessageHeaders.SourcePhysicalHeader] = sourceTopic;
        if (kafkaHeaders is null) return h;
        foreach (var kh in kafkaHeaders)
        {
            var v = Encoding.UTF8.GetString(kh.GetValueBytes());
            switch (kh.Key)
            {
                case MessageHeaders.MessageTypeHeader: h.MessageType = v; break;
                case "MessageId": h.MessageId = v; break;
                case "CorrelationId": h.CorrelationId = v; break;
                case "ContentType": h.ContentType = v; break;
                default: h[kh.Key] = v; break;
            }
        }
        return h;
    }

    private static Acks ParseAcks(string raw) => raw.ToUpperInvariant() switch
    {
        "ALL" => Acks.All,
        "LEADER" => Acks.Leader,
        "NONE" => Acks.None,
        _ => Acks.All,
    };

    private static SecurityProtocol ParseSecurity(string raw) =>
        Enum.Parse<SecurityProtocol>(raw, ignoreCase: true);
}

/// <summary>
/// Per-partition contiguous-watermark tracker. Kafka commits an offset position
/// (not individual messages), so out-of-order acks must be coalesced into a
/// strictly-increasing watermark before commit.
///
/// <para>Usage: call <see cref="RecordDelivered"/> from the consume loop the moment
/// a message is read (this anchors the partition's baseline to the first delivered
/// offset). Then <see cref="Ack"/> at processing completion. The committed offset
/// only advances past contiguous acked offsets — never past a still-pending message.</para>
/// </summary>
internal sealed class OffsetWatermark
{
    private readonly IConsumer<string?, byte[]> _consumer;
    private readonly ConcurrentDictionary<TopicPartition, PartitionState> _partitions = new();
    private readonly object _commitLock = new();

    public OffsetWatermark(IConsumer<string?, byte[]> consumer) => _consumer = consumer;

    /// <summary>
    /// Record that a message at <paramref name="tpo"/> was just delivered to the
    /// consumer. The first call per partition seeds the watermark; subsequent
    /// calls no-op. Must be invoked from the (sequential) consume loop, BEFORE
    /// the message is yielded for processing.
    /// </summary>
    public void RecordDelivered(TopicPartitionOffset tpo)
    {
        // GetOrAdd only invokes the factory on first insert — that's where we seed
        // the baseline. Subsequent deliveries on the same partition are no-ops.
        _partitions.GetOrAdd(tpo.TopicPartition, _ => new PartitionState { NextExpected = tpo.Offset });
    }

    public void Ack(TopicPartitionOffset tpo)
    {
        // RecordDelivered should have run already; but be defensive — if a caller
        // skipped it, seed the baseline from this ack so the watermark still works.
        var state = _partitions.GetOrAdd(tpo.TopicPartition, _ => new PartitionState { NextExpected = tpo.Offset });

        Offset commitPosition;
        lock (state)
        {
            state.Acked.Add(tpo.Offset);
            // Walk every contiguous offset starting at NextExpected.
            while (state.Acked.Remove(state.NextExpected))
            {
                state.NextExpected = state.NextExpected.Value + 1;
            }
            // The commit position is the offset of the NEXT message to read — i.e.
            // the watermark itself. Kafka semantics: committed = first un-processed offset.
            state.Committed = state.NextExpected;
            commitPosition = state.Committed;
        }

        // Commit outside the per-partition lock to avoid serialising across partitions.
        // StoreOffset only stages locally; the underlying consumer flushes asynchronously.
        lock (_commitLock)
        {
            try
            {
                _consumer.StoreOffset(new TopicPartitionOffset(tpo.TopicPartition, commitPosition));
            }
            catch (KafkaException)
            {
                // Storing for a partition we no longer own is benign after a rebalance.
            }
        }
    }

    private sealed class PartitionState
    {
        public Offset NextExpected;          // initialised to first delivered offset
        public Offset Committed;
        public readonly HashSet<Offset> Acked = new();
    }
}

internal sealed class KafkaReceivedMessage : IReceivedMessage
{
    private readonly OffsetWatermark _tracker;
    private readonly TopicPartitionOffset _tpo;
    private readonly bool _autoAck;
    private int _resolved;

    public KafkaReceivedMessage(OffsetWatermark tracker, TopicPartitionOffset tpo, LogicalQueue source, byte[] body, MessageHeaders headers, bool autoAck)
    {
        _tracker = tracker;
        _tpo = tpo;
        _autoAck = autoAck;
        Source = source;
        Body = body;
        Headers = headers;
        DeliveryTag = tpo;
    }

    public ReadOnlyMemory<byte> Body { get; }
    public MessageHeaders Headers { get; }
    public LogicalQueue Source { get; }
    public object DeliveryTag { get; }

    public void MarkAuto()
    {
        if (_autoAck && Interlocked.Exchange(ref _resolved, 1) == 0)
            _tracker.Ack(_tpo);
    }

    public ValueTask AckAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _resolved, 1) == 0)
            _tracker.Ack(_tpo);
        return ValueTask.CompletedTask;
    }

    public ValueTask NackAsync(bool requeue, CancellationToken cancellationToken = default)
    {
        // Always mark resolved so a subsequent Ack does not erroneously advance the
        // offset watermark for a message the consumer has already given up on.
        // Kafka cannot re-insert a message before its current offset, so requeue=true
        // means "do not advance the watermark" — the next consumer rebalance redelivers,
        // stalling the partition until the issue is resolved. requeue=false (dead-letter)
        // is handled by the engine publishing to the Poison topic.
        Interlocked.Exchange(ref _resolved, 1);
        return ValueTask.CompletedTask;
    }

    public object DeserializeBody(IMessageSerializer serializer)
    {
        var typeName = Headers.MessageType
            ?? throw new MessageDeserializationException($"No '{MessageHeaders.MessageTypeHeader}' header on Kafka message from {Source}.");
        return serializer.Deserialize(Body, TypeNameResolver.ResolveOrThrow(typeName));
    }

    public T DeserializeBody<T>(IMessageSerializer serializer) => serializer.Deserialize<T>(Body);
}
