using System.Diagnostics;
using System.Runtime.InteropServices;
using SvcSystems.AI.AgentClientProtocol.Registry.Models;

namespace SvcSystems.AI.AgentClientProtocol.Registry;

/// <summary>Builds ACP process options from the distributions advertised by an agent registry.</summary>
/// <remarks>
/// npx and uvx run the pinned registry package spec and may fetch it into their package caches.
/// Binary archives are downloaded to a local versioned cache before the process options are returned.
/// </remarks>
public sealed class RegistryAgentLauncher : IDisposable
{
    private static readonly TimeSpan CommandProbeTimeout = TimeSpan.FromSeconds(5);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<string, CancellationToken, Task<string?>> _findUsableCommand;
    private readonly RegistryBinaryInstaller _binaryInstaller;
    private readonly RegistryAgentReleaseChannel _releaseChannel;

    /// <summary>Creates a launcher. A supplied <see cref="HttpClient"/> remains caller-owned.</summary>
    public RegistryAgentLauncher(HttpClient? httpClient = null, RegistryAgentLauncherOptions? options = null)
        : this(httpClient, options, FindUsableCommandAsync)
    {
    }

    internal RegistryAgentLauncher(
        HttpClient? httpClient,
        RegistryAgentLauncherOptions? options,
        Func<string, CancellationToken, Task<string?>> findUsableCommand)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _findUsableCommand = findUsableCommand ?? throw new ArgumentNullException(nameof(findUsableCommand));
        _releaseChannel = options?.ReleaseChannel ?? RegistryAgentReleaseChannel.Stable;
        _binaryInstaller = new RegistryBinaryInstaller(_httpClient, options?.AgentsDirectory, _findUsableCommand);
    }

    /// <summary>
    /// Selects the first usable distribution in the configured release channel, preferring npx,
    /// then uvx, then a platform binary. Preview requests never silently fall back to stable.
    /// Package specs are passed through verbatim. Binary archives are cached under
    /// <c>AgentsDirectory/{agent-id}/{channel-version}/{platform}</c>.
    /// </summary>
    public async Task<ProcessOptions> BuildProcessOptionsAsync(
        RegistryAgent agent,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        cancellationToken.ThrowIfCancellationRequested();

        var (version, npx, uvx, binary) = GetDistribution(agent);
        var unavailable = new List<string>();
        if (npx is not null)
        {
            var command = await _findUsableCommand(OperatingSystem.IsWindows() ? "npx.cmd" : "npx", cancellationToken)
                .ConfigureAwait(false);
            if (command is not null)
            {
                return CreatePackageProcessOptions(command, npx.Package, npx.Args, npx.Env, workingDirectory, addNpxYes: true);
            }

            unavailable.Add("npx distribution advertised, but the npx command is missing or failed its --version check");
        }

        if (uvx is not null)
        {
            var command = await _findUsableCommand("uvx", cancellationToken).ConfigureAwait(false);
            if (command is not null)
            {
                return CreatePackageProcessOptions(command, uvx.Package, uvx.Args, uvx.Env, workingDirectory, addNpxYes: false);
            }

            unavailable.Add("uvx distribution advertised, but the uvx command is missing or failed its --version check");
        }

        var platform = GetRegistryPlatform();
        if (platform is not null && binary is not null && binary.Targets.TryGetValue(platform.Value.RegistryName, out var target))
        {
            var executable = await _binaryInstaller.EnsureInstalledAsync(
                agent,
                version,
                platform.Value,
                target,
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
            return new ProcessOptions
            {
                FileName = executable,
                Arguments = target.Args ?? [],
                WorkingDirectory = workingDirectory,
                Environment = ToProcessEnvironment(target.Env),
            };
        }

        if (binary is not null)
        {
            unavailable.Add(platform is null
                ? "binary distribution advertised, but the current operating system or architecture is unsupported"
                : $"binary distribution advertised, but no target exists for {platform.Value.RegistryName}");
        }

        var details = unavailable.Count > 0
            ? string.Join("; ", unavailable)
            : "no supported distribution is advertised";
        throw new RegistryAgentLaunchException(
            $"Cannot launch ACP agent '{agent.Id}': {details}. Install the required package manager, select an available distribution, or update the registry entry.");
    }

    private (string Version, packageDistribution? Npx, packageDistribution? Uvx, binaryDistribution? Binary) GetDistribution(RegistryAgent agent)
    {
        return _releaseChannel switch
        {
            RegistryAgentReleaseChannel.Stable =>
                (agent.Version, agent.Distribution.Npx, agent.Distribution.Uvx, agent.Distribution.Binary),
            RegistryAgentReleaseChannel.Preview when agent.Preview is { } preview =>
                (preview.Version, preview.Distribution.Npx, preview.Distribution.Uvx, preview.Distribution.Binary),
            RegistryAgentReleaseChannel.Preview =>
                throw new RegistryAgentLaunchException($"Agent '{agent.Id}' does not advertise a preview release channel."),
            _ => throw new ArgumentOutOfRangeException(nameof(_releaseChannel), _releaseChannel, "Unknown registry release channel."),
        };
    }

    /// <summary>Releases the internally-created HTTP client, if any.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    internal static ProcessOptions CreatePackageProcessOptions(
        string command,
        string packageSpec,
        IReadOnlyList<string>? args,
        IReadOnlyDictionary<string, string>? environment,
        string? workingDirectory,
        bool addNpxYes)
    {
        var arguments = new List<string>();
        if (addNpxYes)
        {
            arguments.Add("--yes");
        }

        arguments.Add(packageSpec);
        if (args is not null)
        {
            arguments.AddRange(args);
        }

        return new ProcessOptions
        {
            FileName = command,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            Environment = ToProcessEnvironment(environment),
        };
    }

    internal static (string RegistryName, string RuntimeIdentifier)? GetRegistryPlatform(
        string? operatingSystemOverride = null,
        Architecture? architectureOverride = null)
    {
        var architecture = (architectureOverride ?? RuntimeInformation.ProcessArchitecture) switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            _ => null,
        };
        if (architecture is null)
        {
            return null;
        }

        var operatingSystem = operatingSystemOverride ?? (OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "darwin"
            : OperatingSystem.IsLinux() ? "linux"
            : null);
        if (operatingSystem is null)
        {
            return null;
        }

        var ridArchitecture = architecture switch
        {
            "x86_64" => "x64",
            "aarch64" => "arm64",
            _ => throw new InvalidOperationException(),
        };
        var rid = operatingSystem switch
        {
            "windows" => $"win-{ridArchitecture}",
            "darwin" => $"osx-{ridArchitecture}",
            _ => $"linux-{ridArchitecture}",
        };
        return ($"{operatingSystem}-{architecture}", rid);
    }

    private static IReadOnlyDictionary<string, string?> ToProcessEnvironment(IReadOnlyDictionary<string, string>? environment) =>
        environment is null
            ? new Dictionary<string, string?>()
            : environment.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal);

    private static async Task<string?> FindUsableCommandAsync(string command, CancellationToken cancellationToken)
    {
        var path = FindOnPath(command);
        if (path is null)
        {
            return null;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            process.StartInfo.ArgumentList.Add("--version");
            if (!process.Start())
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CommandProbeTimeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                return process.ExitCode == 0 ? path : null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return null;
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? FindOnPath(string command)
    {
        var extensions = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("PATHEXT")?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [".EXE", ".CMD", ".BAT"]
            : [string.Empty];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidateName = Path.HasExtension(command) ? command : command + extension;
                var candidate = Path.Combine(directory.Trim('"'), candidateName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best-effort cleanup after cancellation.
        }
    }
}
