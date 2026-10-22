namespace SvcSystems.AI.AgentClientProtocol.Registry;

/// <summary>Configures local storage for registry binary distributions.</summary>
public sealed class RegistryAgentLauncherOptions
{
    /// <summary>
    /// Gets or sets the root directory for downloaded agent archives and extracted binaries.
    /// Defaults to <c>agents</c> under the process working directory.
    /// </summary>
    public string? AgentsDirectory { get; init; }

    /// <summary>Gets or sets which registry release channel to launch. Defaults to stable.</summary>
    public RegistryAgentReleaseChannel ReleaseChannel { get; init; } = RegistryAgentReleaseChannel.Stable;

    /// <summary>
    /// Gets or sets whether a binary archive without a published SHA-256 checksum is rejected.
    /// Defaults to <see langword="false"/>, which keeps registries that publish no checksum working.
    /// </summary>
    /// <remarks>
    /// A checksumless archive is installed without integrity verification. Set this to
    /// <see langword="true"/> to install only archives whose SHA-256 the registry publishes.
    /// </remarks>
    public bool RequireChecksum { get; init; }
}
