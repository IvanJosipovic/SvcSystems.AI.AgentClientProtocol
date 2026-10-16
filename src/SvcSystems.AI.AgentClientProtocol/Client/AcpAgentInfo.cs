namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Agent identity and negotiated ACP v1 capabilities.</summary>
public sealed record AcpAgentInfo(
    string? Name,
    string? Version,
    int ProtocolVersion,
    AcpAgentCapabilities Capabilities,
    IReadOnlyList<AcpAuthenticationMethod>? AuthenticationMethods = null);

/// <summary>ACP capabilities reported by the agent during initialization.</summary>
public sealed record AcpAgentCapabilities(
    bool LoadSession,
    bool PromptImage,
    bool PromptAudio,
    bool PromptEmbeddedContext,
    AcpSessionCapabilities Sessions,
    bool Logout,
    AcpMcpCapabilities Mcp);

/// <summary>MCP server transport capabilities reported by an ACP agent.</summary>
public sealed record AcpMcpCapabilities(bool Http, bool Sse);

/// <summary>Optional session operations supported by an ACP agent.</summary>
public sealed record AcpSessionCapabilities(
    bool List,
    bool Delete,
    bool Resume,
    bool Close,
    bool AdditionalDirectories);

/// <summary>An authentication method advertised by the ACP agent.</summary>
public sealed record AcpAuthenticationMethod(
    string Id,
    string Name,
    string? Description,
    string? Type,
    IReadOnlyList<string>? Arguments = null,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>Value assigned to a session configuration option.</summary>
public readonly record struct AcpConfigValue(string? ValueId, bool? Boolean)
{
    /// <summary>Creates a selector value using an agent-advertised value id.</summary>
    public static AcpConfigValue FromValueId(string valueId) => new(valueId, null);

    /// <summary>Creates a boolean configuration value.</summary>
    public static AcpConfigValue FromBoolean(bool value) => new(null, value);
}

/// <summary>Session metadata returned by session/list.</summary>
public sealed record AcpSessionInfo(string Id, string WorkingDirectory, string? Title, string? UpdatedAt);

/// <summary>A page of agent sessions.</summary>
public sealed record AcpSessionListPage(IReadOnlyList<AcpSessionInfo> Sessions, string? NextCursor);
