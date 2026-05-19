// Copyright (c) Inverted Software. All rights reserved.

using System.Text.Json;

namespace InvertedSoftware.WorkflowEngine.Queue.Serialization;

/// <summary>
/// <see cref="IMessageSerializer"/> backed by <see cref="System.Text.Json"/>.
/// </summary>
public sealed class JsonMessageSerializer : IMessageSerializer
{
    private readonly JsonSerializerOptions _options;

    public JsonMessageSerializer() : this(DefaultOptions) { }

    public JsonMessageSerializer(JsonSerializerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string ContentType => "application/json";

    public static JsonSerializerOptions DefaultOptions { get; } = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = null,                    // preserve PascalCase to match existing IWorkflowMessage shape
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public ReadOnlyMemory<byte> Serialize<T>(T payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, _options);

    public object Deserialize(ReadOnlyMemory<byte> payload, Type targetType)
    {
        try
        {
            return JsonSerializer.Deserialize(payload.Span, targetType, _options)
                ?? throw new MessageDeserializationException(
                    $"JSON deserialization of '{targetType.FullName}' returned null.");
        }
        catch (JsonException e)
        {
            throw new MessageDeserializationException(
                $"Failed to deserialize message body into '{targetType.FullName}': {e.Message}", e);
        }
    }

    public T Deserialize<T>(ReadOnlyMemory<byte> payload)
    {
        try
        {
            var result = JsonSerializer.Deserialize<T>(payload.Span, _options);
            if (result is null)
                throw new MessageDeserializationException(
                    $"JSON deserialization of '{typeof(T).FullName}' returned null.");
            return result;
        }
        catch (JsonException e)
        {
            throw new MessageDeserializationException(
                $"Failed to deserialize message body into '{typeof(T).FullName}': {e.Message}", e);
        }
    }
}
