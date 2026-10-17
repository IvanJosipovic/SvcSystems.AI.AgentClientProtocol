using System.Text.Json;

namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Configures the client identity and host-side ACP request handlers.</summary>
public sealed class ClientOptions
{
    /// <summary>Identifies this ACP client to the agent.</summary>
    public Implementation ClientInfo { get; init; } = new() { Name = "SvcSystems.AI.AgentClientProtocol", Version = "0.1.0" };

    /// <summary>ACP _meta attached to the initialize request.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>ACP _meta attached to clientCapabilities during initialization.</summary>
    /// <remarks>Advertise only extension behavior that the application implements.</remarks>
    public IReadOnlyDictionary<string, JsonElement>? CapabilityMetadata { get; init; }

    /// <summary>Handlers for incoming custom requests. Method names must begin with an underscore.</summary>
    /// <remarks>Handlers run concurrently, up to <see cref="MaximumConcurrentIncomingRequests"/>, and may send requests.
    /// Excess incoming requests receive a server-busy error. Throw RpcException to return a protocol error.
    /// Returned JSON is serialized immediately after the handler completes and must still be valid then.</remarks>
    public IReadOnlyDictionary<string, Func<JsonElement, CancellationToken, ValueTask<JsonElement>>> ExtensionRequestHandlers { get; init; } =
        new Dictionary<string, Func<JsonElement, CancellationToken, ValueTask<JsonElement>>>();

    /// <summary>Receives method names and exceptions when incoming request handlers fail unexpectedly.</summary>
    /// <remarks>When unset, failures are reported through <see cref="System.Diagnostics.Trace"/>.</remarks>
    public Action<string, Exception>? RequestHandlerException { get; init; }

    /// <summary>Receives unrecognized notifications and supported extension notifications, including authentication status.</summary>
    /// <remarks>Runs on the receive loop, including during initialization. Do not block or wait for ACP operations.
    /// Exceptions terminate the connection and are surfaced through Completion and pending operations.</remarks>
    public Action<ExtensionNotification>? ExtensionNotificationReceived { get; init; }

    /// <summary>Receives connection-wide authentication identity updates, including during initialization.</summary>
    /// <remarks>Runs on the receive loop. Do not block or wait for ACP operations. Exceptions terminate the connection.</remarks>
    public Action<AuthenticationStatus>? AuthenticationStatusChanged { get; init; }

    /// <summary>Receives session id and suggested next prompts, including suggestions delivered after a turn completes.</summary>
    /// <remarks>An empty list clears previous suggestions. Runs on the receive loop; do not block or wait for ACP operations.
    /// Exceptions terminate the connection.</remarks>
    public Action<string, IReadOnlyList<string>>? PromptSuggestionsChanged { get; init; }

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

    /// <summary>Maximum number of agent requests handled concurrently by this client.</summary>
    /// <remarks>Excess requests receive a JSON-RPC server-busy error.</remarks>
    public int MaximumConcurrentIncomingRequests { get; init; } = 32;
}
