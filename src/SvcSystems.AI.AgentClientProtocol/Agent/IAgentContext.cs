using System.Text.Json;

namespace SvcSystems.AI.AgentClientProtocol.Agent;

/// <summary>Operations an ACP agent can use to communicate with its connected client.</summary>
public interface IAgentContext
{
    /// <summary>Sends one ordered progress update for a session.</summary>
    ValueTask SendSessionUpdateAsync(string sessionId, SessionUpdate update, CancellationToken cancellationToken = default);

    /// <summary>Sends a client request and returns its JSON result.</summary>
    Task<JsonElement> SendClientRequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default);

    /// <summary>Sends a client notification without waiting for a response.</summary>
    ValueTask SendClientNotificationAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default);
}
