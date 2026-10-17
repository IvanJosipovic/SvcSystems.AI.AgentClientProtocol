using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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
    private static readonly TimeSpan ArchiveToolTimeout = TimeSpan.FromMinutes(2);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string? _configuredAgentsDirectory;
    private readonly Func<string, CancellationToken, Task<string?>> _findUsableCommand;

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
        _configuredAgentsDirectory = string.IsNullOrWhiteSpace(options?.AgentsDirectory)
            ? null
            : Path.GetFullPath(options.AgentsDirectory);
        _findUsableCommand = findUsableCommand ?? throw new ArgumentNullException(nameof(findUsableCommand));
    }

    /// <summary>
    /// Selects the first usable advertised distribution in npx, uvx, then platform-binary order.
    /// Package versions are passed through verbatim. Binary archives are cached under
    /// <c>AgentsDirectory/{agent-id}/{version}/{platform}</c>.
    /// </summary>
    public async Task<ProcessOptions> BuildProcessOptionsAsync(
        RegistryAgent agent,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        cancellationToken.ThrowIfCancellationRequested();

        var unavailable = new List<string>();
        if (agent.Distribution.Npx is { } npx)
        {
            var command = await _findUsableCommand(OperatingSystem.IsWindows() ? "npx.cmd" : "npx", cancellationToken)
                .ConfigureAwait(false);
            if (command is not null)
            {
                return CreatePackageProcessOptions(command, npx.Package, npx.Args, npx.Env, workingDirectory, addNpxYes: true);
            }

            unavailable.Add("npx distribution advertised, but the npx command is missing or failed its --version check");
        }

        if (agent.Distribution.Uvx is { } uvx)
        {
            var command = await _findUsableCommand("uvx", cancellationToken).ConfigureAwait(false);
            if (command is not null)
            {
                return CreatePackageProcessOptions(command, uvx.Package, uvx.Args, uvx.Env, workingDirectory, addNpxYes: false);
            }

            unavailable.Add("uvx distribution advertised, but the uvx command is missing or failed its --version check");
        }

        var platform = GetRegistryPlatform();
        if (platform is not null && agent.Distribution.Binary is { } binary && binary.Targets.TryGetValue(platform.Value.RegistryName, out var target))
        {
            var executable = await EnsureBinaryInstalledAsync(
                agent,
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

        if (agent.Distribution.Binary is not null)
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

    private async Task<string> EnsureBinaryInstalledAsync(
        RegistryAgent agent,
        (string RegistryName, string RuntimeIdentifier) platform,
        binaryTarget target,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(target.Archive, UriKind.Absolute, out var archiveUri) || archiveUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new RegistryAgentLaunchException($"Agent '{agent.Id}' has an invalid or non-HTTPS binary archive URL.");
        }

        var agentId = ValidatePathSegment(agent.Id, nameof(agent.Id));
        var version = ValidatePathSegment(agent.Version, nameof(agent.Version));
        var platformPath = ValidatePathSegment(platform.RuntimeIdentifier, nameof(platform.RuntimeIdentifier));
        var agentsDirectory = _configuredAgentsDirectory ?? Path.GetFullPath(Path.Combine(workingDirectory ?? Environment.CurrentDirectory, "agents"));
        var agentDirectory = Path.GetFullPath(Path.Combine(agentsDirectory, agentId));
        EnsureContainedPath(agentsDirectory, agentDirectory);
        var installationDirectory = Path.GetFullPath(Path.Combine(agentDirectory, version, platformPath));
        EnsureContainedPath(agentDirectory, installationDirectory);
        var executablePath = GetContainedCommandPath(installationDirectory, target.Cmd);
        var markerPath = Path.Combine(installationDirectory, ".acp-registry-installed");
        if (File.Exists(markerPath) && File.Exists(executablePath))
        {
            EnsureExecutable(executablePath);
            return executablePath;
        }

        Directory.CreateDirectory(agentDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(installationDirectory)!);
        if (Directory.Exists(installationDirectory))
        {
            throw new RegistryAgentLaunchException(
                $"Agent install directory '{installationDirectory}' exists but is incomplete. Remove it or configure a different AgentsDirectory.");
        }

        var stagingDirectory = Path.Combine(agentDirectory, $".{version}.{platformPath}.{Guid.NewGuid():N}.staging");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            var archivePath = Path.Combine(stagingDirectory, "download.archive");
            await DownloadArchiveAsync(archiveUri, archivePath, target.Sha256, agent.Id, cancellationToken).ConfigureAwait(false);
            await ExtractArchiveAsync(archiveUri, archivePath, stagingDirectory, target.Cmd, cancellationToken).ConfigureAwait(false);
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            var stagedExecutable = GetContainedCommandPath(stagingDirectory, target.Cmd);
            if (!File.Exists(stagedExecutable))
            {
                throw new RegistryAgentLaunchException(
                    $"Agent '{agent.Id}' archive did not contain the registry command '{target.Cmd}'.");
            }

            EnsureExecutable(stagedExecutable);
            await File.WriteAllTextAsync(Path.Combine(stagingDirectory, ".acp-registry-installed"), target.Archive, cancellationToken)
                .ConfigureAwait(false);
            Directory.Move(stagingDirectory, installationDirectory);
            return Path.Combine(installationDirectory, Path.GetRelativePath(stagingDirectory, stagedExecutable));
        }
        catch (IOException) when (Directory.Exists(installationDirectory) && File.Exists(markerPath))
        {
            // Another launcher completed the same version while this one was downloading.
            return executablePath;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private async Task DownloadArchiveAsync(
        Uri archiveUri,
        string archivePath,
        string? expectedSha256,
        string agentId,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            archiveUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (expectedSha256 is not null)
        {
            byte[] expected;
            try
            {
                expected = Convert.FromHexString(expectedSha256);
            }
            catch (FormatException exception)
            {
                throw new RegistryAgentLaunchException($"Agent '{agentId}' has an invalid SHA-256 checksum in the registry.", exception);
            }

            if (expected.Length != SHA256.HashSizeInBytes ||
                !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected))
            {
                throw new RegistryAgentLaunchException($"SHA-256 verification failed for agent '{agentId}' binary archive.");
            }
        }
    }

    private async Task ExtractArchiveAsync(
        Uri archiveUri,
        string archivePath,
        string stagingDirectory,
        string command,
        CancellationToken cancellationToken)
    {
        var path = archiveUri.AbsolutePath;
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await ExtractZipAsync(archivePath, stagingDirectory, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            await ExtractGZipTarAsync(archivePath, stagingDirectory, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (path.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tbz2", StringComparison.OrdinalIgnoreCase))
        {
            await ExtractBZip2TarAsync(archivePath, stagingDirectory, cancellationToken).ConfigureAwait(false);
            return;
        }

        var commandPath = GetContainedCommandPath(stagingDirectory, command);
        Directory.CreateDirectory(Path.GetDirectoryName(commandPath)!);
        File.Move(archivePath, commandPath);
    }

    private static async Task ExtractZipAsync(string archivePath, string destination, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outputPath = GetContainedCommandPath(destination, entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixFileType == 0xA000)
            {
                throw new RegistryAgentLaunchException("Binary ZIP archives containing symbolic links are not supported.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await using var input = entry.Open();
            await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ExtractGZipTarAsync(string archivePath, string destination, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outputPath = GetContainedCommandPath(destination, entry.Name);
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
            {
                throw new RegistryAgentLaunchException("Binary tar archives containing links are not supported.");
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            if (entry.DataStream is { } data)
            {
                await data.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ExtractBZip2TarAsync(string archivePath, string destination, CancellationToken cancellationToken)
    {
        var tar = await _findUsableCommand("tar", cancellationToken).ConfigureAwait(false);
        if (tar is null)
        {
            throw new RegistryAgentLaunchException(
                "This binary uses a .tar.bz2 archive, but no system 'tar' command is available. Install tar or use another advertised distribution.");
        }

        var verboseListing = await RunToolAsync(tar, ["-tvjf", archivePath], ArchiveToolTimeout, cancellationToken).ConfigureAwait(false);
        foreach (var line in verboseListing.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line[0] is 'l' or 'h')
            {
                throw new RegistryAgentLaunchException("Binary bzip2 tar archives containing links are not supported.");
            }

            if (line[0] is not ('-' or 'd'))
            {
                throw new RegistryAgentLaunchException("Binary bzip2 tar archives containing special files are not supported.");
            }
        }

        var listing = await RunToolAsync(tar, ["-tjf", archivePath], ArchiveToolTimeout, cancellationToken).ConfigureAwait(false);
        foreach (var member in listing.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            _ = GetContainedCommandPath(destination, member);
        }
        _ = await RunToolAsync(tar, ["-xjf", archivePath, "-C", destination], ArchiveToolTimeout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> RunToolAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new RegistryAgentLaunchException($"Failed to start archive tool '{fileName}'.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new RegistryAgentLaunchException($"Archive tool '{fileName}' failed: {error.Trim()}");
            }

            return output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new RegistryAgentLaunchException($"Archive tool '{fileName}' exceeded its {timeout} time limit.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static string GetContainedCommandPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new RegistryAgentLaunchException($"Binary archive contains an invalid rooted or empty path '{relativePath}'.");
        }

        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsureContainedPath(fullRoot, fullPath);
        return fullPath;
    }

    private static void EnsureContainedPath(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
            !string.Equals(path, fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new RegistryAgentLaunchException($"Path '{path}' escapes configured agent directory '{fullRoot}'.");
        }
    }

    private static string ValidatePathSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains('/') || value.Contains('\\'))
        {
            throw new RegistryAgentLaunchException($"Registry value '{parameterName}' is not a safe directory name.");
        }

        return value;
    }

    private static void EnsureExecutable(string executablePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(executablePath);
            File.SetUnixFileMode(executablePath, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

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
