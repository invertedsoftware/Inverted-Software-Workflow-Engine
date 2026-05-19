// Copyright (c) Inverted Software. All rights reserved.

using System.Collections;

namespace InvertedSoftware.WorkflowEngine.Queue;

/// <summary>
/// Per-message metadata. The well-known properties are projected onto first-class
/// fields; everything else lives in the dictionary and is broker-passed-through.
/// </summary>
public sealed class MessageHeaders : IDictionary<string, string>
{
    /// <summary>Header name carrying the CLR type to deserialize the body into.</summary>
    public const string MessageTypeHeader = "x-wf-message-type";
    /// <summary>Header carrying the source physical destination (for diagnostics).</summary>
    public const string SourcePhysicalHeader = "x-wf-source";

    private readonly Dictionary<string, string> _custom = new(StringComparer.Ordinal);

    /// <summary>Provider-set unique id for the message, if any.</summary>
    public string? MessageId { get; set; }

    /// <summary>Correlation id. The engine sets this to <c>JobID.ToString()</c> on publish.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>MIME type of the body (e.g. <c>application/json</c>).</summary>
    public string? ContentType { get; set; }

    /// <summary>The concrete CLR type the body deserializes to. Set on publish, read on receive.</summary>
    public string? MessageType
    {
        get => _custom.TryGetValue(MessageTypeHeader, out var v) ? v : null;
        set
        {
            if (value is null) _custom.Remove(MessageTypeHeader);
            else _custom[MessageTypeHeader] = value;
        }
    }

    /// <summary>When the message was enqueued (UTC). Provider-set on receive when available.</summary>
    public DateTimeOffset? EnqueuedAtUtc { get; set; }

    /// <summary>1-based delivery attempt counter. Provider-set on receive.</summary>
    public int DeliveryAttempt { get; set; } = 1;

    // ---- IDictionary<string, string> forwarders ------------------------------

    public string this[string key]
    {
        get => _custom[key];
        set => _custom[key] = value;
    }

    public ICollection<string> Keys => _custom.Keys;
    public ICollection<string> Values => _custom.Values;
    public int Count => _custom.Count;
    public bool IsReadOnly => false;

    public void Add(string key, string value) => _custom.Add(key, value);
    public void Add(KeyValuePair<string, string> item) => _custom.Add(item.Key, item.Value);
    public void Clear() => _custom.Clear();
    public bool Contains(KeyValuePair<string, string> item) =>
        ((ICollection<KeyValuePair<string, string>>)_custom).Contains(item);
    public bool ContainsKey(string key) => _custom.ContainsKey(key);
    public void CopyTo(KeyValuePair<string, string>[] array, int arrayIndex) =>
        ((ICollection<KeyValuePair<string, string>>)_custom).CopyTo(array, arrayIndex);
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _custom.GetEnumerator();
    public bool Remove(string key) => _custom.Remove(key);
    public bool Remove(KeyValuePair<string, string> item) =>
        ((ICollection<KeyValuePair<string, string>>)_custom).Remove(item);
    public bool TryGetValue(string key, out string value) => _custom.TryGetValue(key, out value!);
    IEnumerator IEnumerable.GetEnumerator() => _custom.GetEnumerator();
}
