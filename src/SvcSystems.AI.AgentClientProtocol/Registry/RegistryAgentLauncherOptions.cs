namespace SvcSystems.AI.AgentClientProtocol.Registry;

/// <summary>Configures local storage for registry binary distributions.</summary>
public sealed class RegistryAgentLauncherOptions
{
    /// <summary>
    /// Gets or sets the root directory for downloaded agent archives and extracted binaries.
    /// Defaults to <c>agents</c> under the process working directory.
    /// </summary>
    public string? AgentsDirectory { get; init; }
}
