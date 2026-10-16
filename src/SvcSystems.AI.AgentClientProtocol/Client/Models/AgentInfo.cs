using System.Text.Json;
using SvcSystems.AI.AgentClientProtocol.Generated;

namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Agent identity and negotiated ACP v1 capabilities.</summary>
public sealed record AgentInfo(
    Implementation Implementation,
    int ProtocolVersion,
    AgentCapabilities Capabilities,
    JsonElement RawPayload,
    IReadOnlyList<AuthenticationMethod>? AuthenticationMethods = null)
{
    /// <summary>Agent name, forwarded from the schema-generated agentInfo model.</summary>
    public string Name => Implementation.Name;

    /// <summary>Agent version, forwarded from the schema-generated agentInfo model.</summary>
    public string Version => Implementation.Version;

    /// <summary>Agent title, forwarded from the schema-generated agentInfo model.</summary>
    public string? Title => Implementation.Title;

    /// <summary>Whether the agent advertises the connection-wide authStatus push extension.</summary>
    public bool SupportsAuthenticationStatus =>
        Capabilities.Meta is { } meta && meta.TryGetValue("authStatus", out var value) && value.ValueKind == JsonValueKind.Object;
}

/// <summary>An authentication method advertised by the ACP agent.</summary>
public sealed record AuthenticationMethod(
    string Id,
    string Name,
    string? Description,
    string? Type,
    JsonElement RawPayload,
    IReadOnlyList<string>? Arguments = null,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>Value assigned to a session configuration option.</summary>
public readonly record struct ConfigValue(string? ValueId, bool? Boolean)
{
    /// <summary>Creates a selector value using an agent-advertised value id.</summary>
    public static ConfigValue FromValueId(string valueId) => new(valueId, null);

    /// <summary>Creates a boolean configuration value.</summary>
    public static ConfigValue FromBoolean(bool value) => new(null, value);
}
