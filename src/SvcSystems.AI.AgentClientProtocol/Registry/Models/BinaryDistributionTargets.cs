using System.Text.Json;
using System.Text.Json.Serialization;

namespace SvcSystems.AI.AgentClientProtocol.Registry.Models;

/// <summary>Platform-keyed targets for an agent binary distribution.</summary>
public sealed partial class binaryDistribution
{
    /// <summary>Platform-specific binary targets keyed by registry RID (for example, <c>windows-x86_64</c>).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Targets { get; init; }
}
