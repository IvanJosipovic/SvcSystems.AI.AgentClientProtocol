using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using SvcSystems.AI.AgentClientProtocol.Registry.Serialization;

namespace SvcSystems.AI.AgentClientProtocol.Registry.Models;

/// <summary>Platform-keyed targets for an agent binary distribution.</summary>
[JsonConverter(typeof(BinaryDistributionJsonConverter))]
public sealed partial class binaryDistribution
{
    /// <summary>Platform-specific binary targets keyed by registry RID (for example, <c>windows-x86_64</c>).</summary>
    public Dictionary<BinaryDistributionPropertyName, binaryTarget> Targets { get; init; } = new();

    internal bool TryGetTarget(RegistryPlatform platform, out binaryTarget? target) =>
        TryGetTarget(platform.RegistryName, out target);

    internal bool TryGetTarget(string registryPlatformName, out binaryTarget? target)
    {
        if (BinaryDistributionJsonConverter.TryParsePlatformName(registryPlatformName, out var platform))
        {
            return Targets.TryGetValue(platform, out target);
        }

        target = null;
        return false;
    }
}

internal sealed class BinaryDistributionJsonConverter : JsonConverter<binaryDistribution>
{
    public override binaryDistribution Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A registry binary distribution must be an object keyed by platform.");
        }

        var targets = new Dictionary<BinaryDistributionPropertyName, binaryTarget>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("A registry binary target must have a platform key.");
            }

            var registryPlatformName = reader.GetString();
            var platform = PlatformKeyConverter.ReadAsPropertyName(
                ref reader,
                typeof(BinaryDistributionPropertyName),
                options);
            if (!Enum.IsDefined(platform))
            {
                throw new JsonException($"Registry binary distribution contains an unknown platform key '{registryPlatformName}'.");
            }
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
        if (targets.Count == 0)
        {
            throw new JsonException("A registry binary distribution must contain at least one platform target.");
        }

        return new binaryDistribution { Targets = targets };
    }

    public override void Write(Utf8JsonWriter writer, binaryDistribution value, JsonSerializerOptions options)
    {
        if (value.Targets.Count == 0)
        {
            throw new JsonException("A registry binary distribution must contain at least one platform target.");
        }

        writer.WriteStartObject();
        foreach (var (platform, target) in value.Targets)
        {
            PlatformKeyConverter.WriteAsPropertyName(writer, platform, options);
            JsonSerializer.Serialize(writer, target, RegistryJsonContext.Default.binaryTarget);
        }
        writer.WriteEndObject();
    }

    private static JsonConverter<BinaryDistributionPropertyName> PlatformKeyConverter =>
        (JsonConverter<BinaryDistributionPropertyName>)RegistryJsonContext.Default.BinaryDistributionPropertyName.Converter;

    internal static bool TryParsePlatformName(string registryPlatformName, out BinaryDistributionPropertyName platform)
    {
        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStringValue(registryPlatformName);
            }

            var reader = new Utf8JsonReader(buffer.WrittenSpan);
            reader.Read();
            platform = PlatformKeyConverter.Read(
                ref reader,
                typeof(BinaryDistributionPropertyName),
                RegistryJsonContext.Default.Options);
            if (Enum.IsDefined(platform)) return true;

            platform = default;
            return false;
        }
        catch (JsonException)
        {
            platform = default;
            return false;
        }
    }
}
