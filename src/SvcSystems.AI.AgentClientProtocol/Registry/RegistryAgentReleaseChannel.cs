namespace SvcSystems.AI.AgentClientProtocol.Registry;

/// <summary>Release channel to select from a registry agent entry.</summary>
public enum RegistryAgentReleaseChannel
{
    /// <summary>Launch the stable distribution.</summary>
    Stable,

    /// <summary>Launch the advertised preview distribution; does not fall back to stable.</summary>
    Preview,
}
