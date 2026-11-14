using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using SvcSystems.AI.AgentClientProtocol.Registry.Serialization;

namespace SvcSystems.AI.AgentClientProtocol.Registry.Models;

/// <summary>Platform-keyed targets for an agent binary distribution.</summary>
public sealed partial class binaryDistribution
{
    /// <summary>Platform-specific binary targets keyed by registry RID (for example, <c>windows-x86_64</c>).</summary>
    [JsonIgnore]
    public Dictionary<BinaryDistributionPropertyName, binaryTarget> Targets
    {
        get => this;
        init
        {
            Clear();
            foreach (var (platform, target) in value)
            {
                this[platform] = target;
            }
        }
    }

    internal bool TryGetTarget(RegistryPlatform platform, out binaryTarget? target) =>
        TryGetTarget(platform.RegistryName, out target);

    internal bool TryGetTarget(string registryPlatformName, out binaryTarget? target)
    {
        if (TryParsePlatformName(registryPlatformName, out var platform))
        {
            return TryGetValue(platform, out target);
        }

        target = null;
        return false;
    }

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
            platform = ((JsonConverter<BinaryDistributionPropertyName>)RegistryJsonContext.Default.BinaryDistributionPropertyName.Converter)
                .Read(ref reader, typeof(BinaryDistributionPropertyName), RegistryJsonContext.Default.Options);
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
