// Copyright (c) Inverted Software. All rights reserved.

namespace InvertedSoftware.WorkflowEngine.Queue;

/// <summary>
/// Serializes and deserializes message bodies. Providers are agnostic of the
/// concrete format — the engine owns serialization end-to-end.
/// </summary>
public interface IMessageSerializer
{
    /// <summary>MIME type produced/consumed by this serializer (e.g. <c>application/json</c>).</summary>
    string ContentType { get; }

    /// <summary>Serialize <paramref name="payload"/> to bytes.</summary>
    ReadOnlyMemory<byte> Serialize<T>(T payload);

    /// <summary>Deserialize <paramref name="payload"/> into an instance of <paramref name="targetType"/>.</summary>
    object Deserialize(ReadOnlyMemory<byte> payload, Type targetType);

    /// <summary>Deserialize <paramref name="payload"/> into <typeparamref name="T"/>.</summary>
    T Deserialize<T>(ReadOnlyMemory<byte> payload);
}
