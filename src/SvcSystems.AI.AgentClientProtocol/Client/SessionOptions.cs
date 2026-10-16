namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Options used to create or load an ACP session.</summary>
public sealed class SessionOptions
{
    /// <summary>Absolute working directory for the session.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Additional absolute workspace directories, when supported by the agent.</summary>
    public IReadOnlyList<string> AdditionalDirectories { get; init; } = [];

    /// <summary>MCP servers configured for this session.</summary>
    public IReadOnlyList<McpServerOptions> McpServers { get; init; } = [];
}
