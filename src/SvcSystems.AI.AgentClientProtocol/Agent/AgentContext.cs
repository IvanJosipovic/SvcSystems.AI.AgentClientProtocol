using System.Text.Json;
using SvcSystems.AI.AgentClientProtocol.JsonRpc;
using SvcSystems.AI.AgentClientProtocol.Serialization;

namespace SvcSystems.AI.AgentClientProtocol.Agent;

internal sealed class AgentContext(JsonRpcConnection connection) : IAgentContext
{
    public ValueTask SendSessionUpdateAsync(string sessionId, SessionUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var notification = new SessionNotification { SessionId = sessionId, Update = update };
        var parameters = JsonSerializer.SerializeToUtf8Bytes(notification, JsonContext.Default.SessionNotification);
        return connection.SendNotificationAsync("session/update", parameters, cancellationToken);
    }

    public Task<JsonElement> SendClientRequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ValidateParameters(parameters);
        return connection.SendRequestAsync(method, WireJson.WriteJsonValue(parameters), cancellationToken);
    }

    public ValueTask SendClientNotificationAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ValidateParameters(parameters);
        return connection.SendNotificationAsync(method, WireJson.WriteJsonValue(parameters), cancellationToken);
    }

    private static void ValidateParameters(JsonElement parameters)
    {
        if (parameters.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            throw new ArgumentException("JSON-RPC parameters must be an object or array.", nameof(parameters));
        }
    }
}
