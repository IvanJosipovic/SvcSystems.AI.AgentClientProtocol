namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Configures the client identity and host-side ACP request handlers.</summary>
public sealed class AcpClientOptions
{
    /// <summary>Identifies this ACP client to the agent.</summary>
    public AcpImplementationInfo ClientInfo { get; init; } = new("SvcSystems.AI.AgentClientProtocol", "0.1.0");

    /// <summary>Handles agent requests for user permission. Missing handlers reject safely when possible.</summary>
    public IAcpPermissionHandler? PermissionHandler { get; init; }

    /// <summary>Host filesystem operations. When set, the client advertises the supported ACP fs capabilities.</summary>
    public IAcpFileSystemHandler? FileSystemHandler { get; init; }

    /// <summary>Host terminal operations. When set, the client advertises the ACP terminal capability.</summary>
    public IAcpTerminalHandler? TerminalHandler { get; init; }

    /// <summary>Host elicitation support. Supported modes are advertised independently.</summary>
    public IAcpElicitationHandler? ElicitationHandler { get; init; }

    /// <summary>Host-provided interactive terminal authentication support.</summary>
    public IAcpTerminalAuthenticationHandler? TerminalAuthenticationHandler { get; init; }

    /// <summary>Receives completion notifications for URL-based elicitation.</summary>
    public Action<AcpElicitationCompleted>? ElicitationCompleted { get; init; }

    /// <summary>Advertises and accepts ACP context-compaction updates.</summary>
    public bool SupportsCompactionUpdates { get; init; } = true;

    /// <summary>Advertises and accepts ACP user-facing notice updates.</summary>
    public bool SupportsNoticeUpdates { get; init; } = true;

    /// <summary>Advertises support for boolean session configuration options.</summary>
    public bool SupportsBooleanSessionConfigOptions { get; init; } = true;
}

/// <summary>Describes an ACP implementation.</summary>
public sealed record AcpImplementationInfo(string Name, string Version);
