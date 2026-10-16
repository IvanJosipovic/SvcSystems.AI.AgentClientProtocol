using SvcSystems.AI.AgentClientProtocol.Protocol.Generated;

namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Configures the client identity and host-side ACP request handlers.</summary>
public sealed class ClientOptions
{
    /// <summary>Identifies this ACP client to the agent.</summary>
    public Implementation ClientInfo { get; init; } = new() { Name = "SvcSystems.AI.AgentClientProtocol", Version = "0.1.0" };

    /// <summary>Handles agent requests for user permission. Missing handlers reject safely when possible.</summary>
    public IPermissionHandler? PermissionHandler { get; init; }

    /// <summary>Host filesystem operations. When set, the client advertises the supported ACP fs capabilities.</summary>
    public IFileSystemHandler? FileSystemHandler { get; init; }

    /// <summary>Host terminal operations. When set, the client advertises the ACP terminal capability.</summary>
    public ITerminalHandler? TerminalHandler { get; init; }

    /// <summary>Host elicitation support. Supported modes are advertised independently.</summary>
    public IElicitationHandler? ElicitationHandler { get; init; }

    /// <summary>Host-provided interactive terminal authentication support.</summary>
    public ITerminalAuthenticationHandler? TerminalAuthenticationHandler { get; init; }

    /// <summary>Receives completion notifications for URL-based elicitation.</summary>
    public Action<ElicitationCompleted>? ElicitationCompleted { get; init; }

    /// <summary>Advertises and accepts ACP context-compaction updates.</summary>
    public bool SupportsCompactionUpdates { get; init; } = true;

    /// <summary>Advertises and accepts ACP user-facing notice updates.</summary>
    public bool SupportsNoticeUpdates { get; init; } = true;

    /// <summary>Advertises support for boolean session configuration options.</summary>
    public bool SupportsBooleanSessionConfigOptions { get; init; } = true;

    /// <summary>Maximum number of unread session updates buffered per session before its update stream fails.</summary>
    public int MaximumBufferedSessionUpdates { get; init; } = 1024;
}

