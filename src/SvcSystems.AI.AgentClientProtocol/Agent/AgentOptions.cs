namespace SvcSystems.AI.AgentClientProtocol.Agent;

/// <summary>Resource limits for an ACP agent host.</summary>
public sealed class AgentOptions
{
    /// <summary>Maximum number of client requests handled concurrently. Defaults to 32.</summary>
    public int MaximumConcurrentIncomingRequests { get; init; } = 32;

    /// <summary>Maximum UTF-8 size of one JSON-RPC input frame. Defaults to 8 MiB.</summary>
    public int MaximumMessageBytes { get; init; } = 8 * 1024 * 1024;
}
