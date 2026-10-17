namespace SvcSystems.AI.AgentClientProtocol.Registry;

/// <summary>Thrown when no advertised registry distribution can be launched.</summary>
public sealed class RegistryAgentLaunchException : InvalidOperationException
{
    /// <summary>Creates an exception with an actionable launch failure message.</summary>
    public RegistryAgentLaunchException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with an actionable launch failure message and cause.</summary>
    public RegistryAgentLaunchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
