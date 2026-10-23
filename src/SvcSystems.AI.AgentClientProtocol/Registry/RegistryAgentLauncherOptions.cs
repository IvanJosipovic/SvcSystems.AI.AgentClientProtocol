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

    /// <summary>
    /// Gets or sets the maximum allowed size of a downloaded binary archive, in bytes.
    /// Archives exceeding this limit are rejected. Defaults to 100 MB.
    /// </summary>
    /// <remarks>
    /// Protects against zip/tar bombs: an attacker-controlled archive that expands to a
    /// massive size while the compressed download remains small. Set to a value appropriate
    /// for your expected agent binary sizes.
    /// </remarks>
    public long MaxArchiveBytes { get; init; } = 100_000_000;

    /// <summary>
    /// Gets or sets the maximum number of entries (files + directories) allowed in an archive.
    /// Archives exceeding this limit are rejected. Defaults to 10,000.
    /// </summary>
    /// <remarks>
    /// Protects against archive DoS via excessive entry counts that consume memory during extraction.
    /// </remarks>
    public int MaxArchiveEntries { get; init; } = 10_000;

    /// <summary>
    /// Gets or sets the maximum allowed compression ratio (uncompressed size / compressed size).
    /// Archives exceeding this ratio are rejected. Defaults to 100.0.
    /// </summary>
    /// <remarks>
    /// Protects against zip/tar bombs where a tiny compressed archive expands massively.
    /// A ratio of 100 means the uncompressed content may be at most 100x the compressed size.
    /// </remarks>
    public double MaxCompressionRatio { get; init; } = 100.0;
}
