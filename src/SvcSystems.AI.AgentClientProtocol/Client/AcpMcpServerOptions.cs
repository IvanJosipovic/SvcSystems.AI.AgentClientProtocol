namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Base configuration for an MCP server passed to an ACP agent for a session.</summary>
public abstract record AcpMcpServerOptions(string Name);

/// <summary>MCP server started by the agent over stdio.</summary>
public sealed record AcpStdioMcpServerOptions(
    string Name,
    string Command,
    IReadOnlyList<string>? Arguments = null,
    IReadOnlyDictionary<string, string>? Environment = null) : AcpMcpServerOptions(Name);

/// <summary>MCP server accessed by the agent over HTTP.</summary>
public sealed record AcpHttpMcpServerOptions(
    string Name,
    string Url,
    IReadOnlyDictionary<string, string>? Headers = null) : AcpMcpServerOptions(Name);

/// <summary>MCP server accessed by the agent over server-sent events.</summary>
public sealed record AcpSseMcpServerOptions(
    string Name,
    string Url,
    IReadOnlyDictionary<string, string>? Headers = null) : AcpMcpServerOptions(Name);
