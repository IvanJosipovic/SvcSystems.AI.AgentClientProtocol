namespace SvcSystems.AI.AgentClientProtocol.Agent;

/// <summary>Application callbacks that implement an ACP agent.</summary>
public interface IAcpAgentHandler
{
    /// <summary>Negotiates protocol version and reports agent capabilities.</summary>
    ValueTask<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken);

    /// <summary>Creates an agent session and returns its identifier.</summary>
    ValueTask<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken);

    /// <summary>Processes a prompt. Stream progress through <paramref name="context"/>.</summary>
    ValueTask<PromptResponse> PromptAsync(PromptRequest request, IAcpAgentContext context, CancellationToken cancellationToken);

    /// <summary>Loads and replays an existing session when the agent advertises load support.</summary>
    ValueTask<LoadSessionResponse> LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromException<LoadSessionResponse>(Unsupported("session/load"));

    /// <summary>Resumes an existing session without replaying prior messages when supported.</summary>
    ValueTask<ResumeSessionResponse> ResumeSessionAsync(ResumeSessionRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromException<ResumeSessionResponse>(Unsupported("session/resume"));

    /// <summary>Lists sessions when the agent advertises session listing.</summary>
    ValueTask<ListSessionsResponse> ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromException<ListSessionsResponse>(Unsupported("session/list"));

    /// <summary>Deletes an existing session when the agent advertises deletion.</summary>
    ValueTask<DeleteSessionResponse> DeleteSessionAsync(DeleteSessionRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromException<DeleteSessionResponse>(Unsupported("session/delete"));

    /// <summary>Closes an active session when the agent advertises close support.</summary>
    ValueTask<CloseSessionResponse> CloseSessionAsync(CloseSessionRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromException<CloseSessionResponse>(Unsupported("session/close"));

    /// <summary>Changes the current mode when the agent advertises session modes.</summary>
    ValueTask<SetSessionModeResponse> SetSessionModeAsync(SetSessionModeRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromException<SetSessionModeResponse>(Unsupported("session/set_mode"));

    /// <summary>Changes a session configuration option when the agent advertises configuration options.</summary>
    ValueTask<SetSessionConfigOptionResponse> SetSessionConfigOptionAsync(
        SetSessionConfigOptionRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<SetSessionConfigOptionResponse>(Unsupported("session/set_config_option"));

    /// <summary>Handles an agent-supported authentication method.</summary>
    ValueTask<AuthenticateResponse> AuthenticateAsync(AuthenticateRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromException<AuthenticateResponse>(Unsupported("authenticate"));

    /// <summary>Logs out when the agent advertises logout.</summary>
    ValueTask<LogoutResponse> LogoutAsync(LogoutRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromException<LogoutResponse>(Unsupported("logout"));

    private static RpcException Unsupported(string method) =>
        new(-32601, "The handler does not implement this optional request.", method);
}
