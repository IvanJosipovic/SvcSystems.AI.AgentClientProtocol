namespace SvcSystems.AI.AgentClientProtocol;

using SvcSystems.AI.AgentClientProtocol.Protocol.Generated;

/// <summary>Consumer-facing ACP client operations.</summary>
public interface IClient : IAsyncDisposable
{
    /// <summary>Agent identity and capabilities negotiated during initialization.</summary>
    AgentInfo Agent { get; }

    /// <summary>Creates a new ACP session.</summary>
    Task<Session> CreateSessionAsync(SessionOptions options, CancellationToken cancellationToken = default);

    /// <summary>Loads an existing session when the agent advertises session loading.</summary>
    Task<Session> LoadSessionAsync(string sessionId, SessionOptions options, CancellationToken cancellationToken = default);

    /// <summary>Authenticates using a method advertised during initialization.</summary>
    Task AuthenticateAsync(string methodId, CancellationToken cancellationToken = default);

    /// <summary>Ends the current authenticated state when the agent supports logout.</summary>
    Task LogoutAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists sessions when the agent advertises session/list.</summary>
    Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workingDirectory = null, string? cursor = null, CancellationToken cancellationToken = default);

    /// <summary>Lists sessions and returns the opaque pagination cursor when the agent provides one.</summary>
    Task<ListSessionsResponse> ListSessionsPageAsync(string? workingDirectory = null, string? cursor = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes a session when the agent advertises session/delete.</summary>
    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Resumes a session without replaying its previous prompt history.</summary>
    Task<Session> ResumeSessionAsync(string sessionId, SessionOptions options, CancellationToken cancellationToken = default);

    /// <summary>Closes an active session when the agent advertises session/close.</summary>
    Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}
