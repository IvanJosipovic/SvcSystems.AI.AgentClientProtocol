using System.Text.Json;
using System.Text.Json.Serialization;
using SvcSystems.AI.AgentClientProtocol.Registry.Serialization;

namespace SvcSystems.AI.AgentClientProtocol.Registry.Models;

/// <summary>Platform-keyed targets for an agent binary distribution.</summary>
[JsonConverter(typeof(BinaryDistributionJsonConverter))]
public sealed partial class binaryDistribution
{
    /// <summary>Platform-specific binary targets keyed by registry RID (for example, <c>windows-x86_64</c>).</summary>
    public Dictionary<string, binaryTarget> Targets { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class BinaryDistributionJsonConverter : JsonConverter<binaryDistribution>
{
    public override binaryDistribution Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A registry binary distribution must be an object keyed by platform.");
        }

        var targets = new Dictionary<string, binaryTarget>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("A registry binary target must have a platform key.");
            }

            var platform = reader.GetString()!;
            if (!reader.Read())
            {
                throw new JsonException($"Registry binary target '{platform}' has no value.");
            }

            var target = JsonSerializer.Deserialize(ref reader, RegistryJsonContext.Default.binaryTarget)
                ?? throw new JsonException($"Registry binary target '{platform}' is null.");
            targets[platform] = target;
        }

        if (reader.TokenType != JsonTokenType.EndObject)
        {
            throw new JsonException("The registry binary distribution object was not terminated.");
        }

        return new binaryDistribution { Targets = targets };
    }

    public override void Write(Utf8JsonWriter writer, binaryDistribution value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (platform, target) in value.Targets)
        {
            writer.WritePropertyName(platform);
            JsonSerializer.Serialize(writer, target, RegistryJsonContext.Default.binaryTarget);
        }
        writer.WriteEndObject();
    }
}
