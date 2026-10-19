using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SvcSystems.AI.AgentClientProtocol.Serialization;

/// <summary>
/// Exposes the serializer contract the client and the agent use on the wire, so callers can read and
/// write ACP models -- including payloads for extension methods -- with identical settings.
/// </summary>
public static class AcpJson
{
    /// <summary>The serializer options generated for the ACP models.</summary>
    public static JsonSerializerOptions Options => JsonContext.Default.Options;

    /// <summary>Returns the generated type metadata for an ACP model.</summary>
    /// <typeparam name="T">An ACP model registered in the generated serializer context.</typeparam>
    /// <returns>The type metadata used to (de)serialize <typeparamref name="T"/>.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> is not part of the ACP model set.</exception>
    public static JsonTypeInfo<T> TypeInfo<T>() =>
        JsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException($"'{typeof(T)}' is not part of the generated ACP serializer context.");

    /// <summary>Serializes an ACP model to a <see cref="JsonElement"/>.</summary>
    /// <typeparam name="T">An ACP model registered in the generated serializer context.</typeparam>
    /// <param name="value">The model to serialize.</param>
    /// <returns>The serialized model.</returns>
    public static JsonElement SerializeToElement<T>(T value) => JsonSerializer.SerializeToElement(value, TypeInfo<T>());

    /// <summary>Serializes an ACP model to UTF-8 JSON.</summary>
    /// <typeparam name="T">An ACP model registered in the generated serializer context.</typeparam>
    /// <param name="value">The model to serialize.</param>
    /// <returns>The serialized model.</returns>
    public static byte[] SerializeToUtf8Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, TypeInfo<T>());

    /// <summary>Deserializes a <see cref="JsonElement"/> into an ACP model.</summary>
    /// <typeparam name="T">An ACP model registered in the generated serializer context.</typeparam>
    /// <param name="element">The JSON to deserialize.</param>
    /// <returns>The deserialized model, or <see langword="null"/> when the JSON is <c>null</c>.</returns>
    public static T? Deserialize<T>(JsonElement element) => element.Deserialize(TypeInfo<T>());

    /// <summary>Deserializes UTF-8 JSON into an ACP model.</summary>
    /// <typeparam name="T">An ACP model registered in the generated serializer context.</typeparam>
    /// <param name="utf8Json">The JSON to deserialize.</param>
    /// <returns>The deserialized model, or <see langword="null"/> when the JSON is <c>null</c>.</returns>
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json) => JsonSerializer.Deserialize(utf8Json, TypeInfo<T>());
}
