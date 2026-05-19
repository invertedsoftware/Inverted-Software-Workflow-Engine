// Copyright (c) Inverted Software. All rights reserved.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace InvertedSoftware.WorkflowEngine.Queue.InMemory;

/// <summary>
/// In-memory <see cref="IQueueProvider"/> backed by <see cref="Channel{T}"/>
/// per logical queue. Atomic publish-batch (single lock); all messages stay in
/// memory until ack'd. Use for unit tests and the console sample only.
/// </summary>
public sealed class InMemoryQueueProvider : IQueueProvider
{
    private readonly InMemoryQueueOptions _options;
    private readonly ConcurrentDictionary<LogicalQueue, Channel<InMemoryReceivedMessage>> _channels = new();
    private readonly object _batchLock = new();
    private int _deliveryTagCounter;

    public InMemoryQueueProvider() : this(new InMemoryQueueOptions()) { }

    public InMemoryQueueProvider(InMemoryQueueOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Name => "InMemory";

    private Channel<InMemoryReceivedMessage> GetOrCreate(LogicalQueue queue) =>
        _channels.GetOrAdd(queue, _ => Channel.CreateBounded<InMemoryReceivedMessage>(
            new BoundedChannelOptions(_options.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            }));

    public ValueTask<QueueHealth> CheckHealthAsync(string jobName, int tier = 0, CancellationToken cancellationToken = default)
    {
        long Depth(LogicalQueueKind kind) =>
            _channels.TryGetValue(new LogicalQueue(jobName, kind, tier), out var ch) ? ch.Reader.Count : 0;

        return new ValueTask<QueueHealth>(new QueueHealth(
            MainAvailable: true,
            ErrorAvailable: true,
            PoisonAvailable: true,
            CompletedAvailable: true,
            ApproximateMainDepth: Depth(LogicalQueueKind.Main),
            Diagnostic: tier == 0 ? "InMemory" : $"InMemory tier {tier}"));
    }

    public async ValueTask PublishAsync(
        LogicalQueue destination,
        ReadOnlyMemory<byte> body,
        MessageHeaders headers,
        CancellationToken cancellationToken = default)
    {
        var channel = GetOrCreate(destination);
        var msg = NewMessage(destination, body, headers);
        await channel.Writer.WriteAsync(msg, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask PublishBatchAsync(
        IReadOnlyList<OutgoingMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (messages is null) throw new ArgumentNullException(nameof(messages));
        if (messages.Count == 0) return;

        // Atomic enqueue across multiple destinations. Bounded channels can block on Write
        // when full, so we WaitToWriteAsync first and only commit inside the lock.
        foreach (var m in messages)
        {
            var ch = GetOrCreate(m.Destination);
            if (!await ch.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
                throw new QueueProviderException($"Channel for {m.Destination} is closed.");
        }

        lock (_batchLock)
        {
            foreach (var m in messages)
            {
                var ch = GetOrCreate(m.Destination);
                var msg = NewMessage(m.Destination, m.Body, m.Headers);
                if (!ch.Writer.TryWrite(msg))
                    throw new QueueProviderException($"Failed to enqueue to {m.Destination} (channel full).");
            }
        }
    }

    public async IAsyncEnumerable<IReceivedMessage> ConsumeAsync(
        string jobName,
        ConsumeOptions options,
        int tier = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var queue = new LogicalQueue(jobName, LogicalQueueKind.Main, tier);
        var channel = GetOrCreate(queue);

        await foreach (var message in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            message.Reset();
            if (options.AutoAck)
            {
                yield return message;
                await message.AckAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                message.OnNackRequeue = async (m, ct) =>
                {
                    // Best-effort: requeue to head not possible with Channel; append to tail.
                    // Use WriteAsync (not TryWrite) so we wait for capacity instead of
                    // silently dropping the message when the bounded channel is full.
                    var ch = GetOrCreate(m.Source);
                    await ch.Writer.WriteAsync(m, ct).ConfigureAwait(false);
                };
                yield return message;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var ch in _channels.Values)
            ch.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private InMemoryReceivedMessage NewMessage(LogicalQueue destination, ReadOnlyMemory<byte> body, MessageHeaders headers)
    {
        var tag = Interlocked.Increment(ref _deliveryTagCounter);
        var copiedHeaders = CopyHeaders(headers);
        copiedHeaders.EnqueuedAtUtc ??= DateTimeOffset.UtcNow;
        return new InMemoryReceivedMessage(destination, body, copiedHeaders, tag);
    }

    private static MessageHeaders CopyHeaders(MessageHeaders source)
    {
        var copy = new MessageHeaders
        {
            MessageId = source.MessageId,
            CorrelationId = source.CorrelationId,
            ContentType = source.ContentType,
            MessageType = source.MessageType,
            EnqueuedAtUtc = source.EnqueuedAtUtc,
            DeliveryAttempt = source.DeliveryAttempt,
        };
        foreach (var kv in source)
            if (!copy.ContainsKey(kv.Key))
                copy.Add(kv.Key, kv.Value);
        return copy;
    }
}

internal sealed class InMemoryReceivedMessage : IReceivedMessage
{
    private int _resolved;            // 0 = pending, 1 = ack'd or nack'd

    public InMemoryReceivedMessage(LogicalQueue source, ReadOnlyMemory<byte> body, MessageHeaders headers, int tag)
    {
        Source = source;
        Body = body;
        Headers = headers;
        DeliveryTag = tag;
    }

    public ReadOnlyMemory<byte> Body { get; }
    public MessageHeaders Headers { get; }
    public LogicalQueue Source { get; }
    public object DeliveryTag { get; }

    /// <summary>Provider sets this so Nack-with-requeue can re-enqueue.</summary>
    public Func<InMemoryReceivedMessage, CancellationToken, ValueTask>? OnNackRequeue { get; set; }

    public void Reset() => _resolved = 0;

    public ValueTask AckAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _resolved, 1);
        return ValueTask.CompletedTask;
    }

    public async ValueTask NackAsync(bool requeue, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _resolved, 1) == 1)
            return;
        if (requeue && OnNackRequeue is not null)
        {
            Headers.DeliveryAttempt++;
            await OnNackRequeue(this, cancellationToken).ConfigureAwait(false);
        }
    }

    public object DeserializeBody(IMessageSerializer serializer)
    {
        var typeName = Headers.MessageType
            ?? throw new MessageDeserializationException(
                $"Received message from {Source} has no '{MessageHeaders.MessageTypeHeader}' header.");
        return serializer.Deserialize(Body, TypeNameResolver.ResolveOrThrow(typeName));
    }

    public T DeserializeBody<T>(IMessageSerializer serializer) => serializer.Deserialize<T>(Body);
}
