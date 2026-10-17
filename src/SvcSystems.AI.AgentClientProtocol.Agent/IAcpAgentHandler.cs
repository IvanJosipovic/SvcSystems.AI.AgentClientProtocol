using SvcSystems.AI.AgentClientProtocol.Agent.Models;

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
}
