namespace SvcSystems.AI.AgentClientProtocol;

using System.Text.Json;

/// <summary>Consumer-facing ACP client operations.</summary>
public interface IClient : IAsyncDisposable
{
    /// <summary>Agent identity and capabilities negotiated during initialization.</summary>
    InitializeResponse Agent { get; }

    /// <summary>Completes when the underlying connection closes; the result contains the transport failure, if any.</summary>
    Task<Exception?> Completion { get; }

    /// <summary>Raised once when the connection ends because of a transport or protocol failure.</summary>
    /// <remarks>Not raised for a clean shutdown. The same failure is available as the result of
    /// <see cref="Completion"/>. Handlers run on a thread-pool thread and must not block.</remarks>
    event EventHandler<TransportFaultedEventArgs>? TransportFaulted;

    /// <summary>Latest reported authentication identity, or null when the agent has not reported one.</summary>
    AuthenticationStatus? AuthenticationStatus { get; }

    /// <summary>Sends a custom or legacy adapter request and returns its complete JSON result.</summary>
    /// <remarks>Parameters must be an object or array. Capability checks are the caller's responsibility.</remarks>
    Task<JsonElement> SendExtensionRequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default);

    /// <summary>Sends a custom or legacy adapter notification without expecting a response.</summary>
    ValueTask SendExtensionNotificationAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default);

    /// <summary>Creates a new ACP session.</summary>
    Task<Session> CreateSessionAsync(SessionOptions options, CancellationToken cancellationToken = default);

    /// <summary>Loads an existing session when the agent advertises session loading.</summary>
    Task<Session> LoadSessionAsync(string sessionId, SessionOptions options, CancellationToken cancellationToken = default);

    /// <summary>Authenticates using a method advertised during initialization.</summary>
    Task AuthenticateAsync(string methodId, CancellationToken cancellationToken = default);

    /// <summary>Ends the current authenticated state when the agent supports logout.</summary>
    Task LogoutAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists sessions when the agent advertises session/list.</summary>
    /// <remarks>Convenience projection of <see cref="ListSessionsPageAsync"/> that discards the pagination cursor.</remarks>
    Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workingDirectory = null, string? cursor = null, CancellationToken cancellationToken = default);

    /// <summary>Lists one page of sessions, including the opaque cursor for the next page when the agent provides one.</summary>
    Task<ListSessionsResponse> ListSessionsPageAsync(string? workingDirectory = null, string? cursor = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes a session when the agent advertises session/delete.</summary>
    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Resumes a session without replaying its previous prompt history.</summary>
    Task<Session> ResumeSessionAsync(string sessionId, SessionOptions options, CancellationToken cancellationToken = default);

    /// <summary>Closes an active session when the agent advertises session/close.</summary>
    Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}
