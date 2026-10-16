using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace SvcSystems.AI.AgentClientProtocol.IntegrationTests;

internal sealed record TestyBinaries(string TestyPath, string? McpEchoPath);

internal static class TestyBinaryProvider
{
    private const string RustSdkCommit = "f5c0eef0972bbda8e75a151aeb088642911b3568";
    private static readonly HttpClient HttpClient = new();
    private static readonly SemaphoreSlim BuildLock = new(1, 1);
    private static Task<TestyBinaries?>? _buildTask;

    public static async Task<TestyBinaries?> GetAsync()
    {
        var configuredTesty = ExistingFile(Environment.GetEnvironmentVariable("ACP_TESTY_BIN"));
        var configuredMcpEcho = ExistingFile(Environment.GetEnvironmentVariable("ACP_MCP_ECHO_BIN"));
        if (configuredTesty is not null && configuredMcpEcho is not null)
        {
            return new TestyBinaries(configuredTesty, configuredMcpEcho);
        }

        if (string.Equals(Environment.GetEnvironmentVariable("ACP_TESTY_AUTO_DOWNLOAD"), "false", StringComparison.OrdinalIgnoreCase))
        {
            return configuredTesty is null ? null : new TestyBinaries(configuredTesty, configuredMcpEcho);
        }

        var binaries = await GetOrBuildAsync().ConfigureAwait(false);
        if (binaries is null)
        {
            return configuredTesty is null ? null : new TestyBinaries(configuredTesty, configuredMcpEcho);
        }

        return new TestyBinaries(configuredTesty ?? binaries.TestyPath, configuredMcpEcho ?? binaries.McpEchoPath);
    }

    private static Task<TestyBinaries?> GetOrBuildAsync()
    {
        lock (BuildLock)
        {
            if (_buildTask is { IsFaulted: true } or { IsCanceled: true })
            {
                _buildTask = null;
            }
            return _buildTask ??= BuildAsync();
        }
    }

    private static async Task<TestyBinaries?> BuildAsync()
    {
        await BuildLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var cacheRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SvcSystems",
                "AcpTesty",
                RustSdkCommit,
                RuntimeInformation.RuntimeIdentifier);
            var sourceRoot = Path.Combine(cacheRoot, "rust-sdk");
            var targetRoot = Path.Combine(cacheRoot, "target");
            var binaryDirectory = Path.Combine(targetRoot, "debug");
            var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            var testyPath = Path.Combine(binaryDirectory, "testy" + extension);
            var mcpEchoPath = Path.Combine(binaryDirectory, "mcp-echo-server" + extension);

            if (File.Exists(testyPath) && File.Exists(mcpEchoPath))
            {
                return new TestyBinaries(testyPath, mcpEchoPath);
            }

            using var lockFile = await AcquireCacheLockAsync(Path.Combine(cacheRoot, "build.lock")).ConfigureAwait(false);
            if (File.Exists(testyPath) && File.Exists(mcpEchoPath))
            {
                return new TestyBinaries(testyPath, mcpEchoPath);
            }

            if (!CargoIsAvailable())
            {
                return null;
            }

            Directory.CreateDirectory(cacheRoot);
            if (!File.Exists(Path.Combine(sourceRoot, "Cargo.toml")))
            {
                await DownloadSourceAsync(cacheRoot, sourceRoot).ConfigureAwait(false);
            }

            Directory.CreateDirectory(targetRoot);
            var startInfo = new ProcessStartInfo("cargo")
            {
                WorkingDirectory = sourceRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("build");
            startInfo.ArgumentList.Add("--manifest-path");
            startInfo.ArgumentList.Add(Path.Combine(sourceRoot, "Cargo.toml"));
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add("agent-client-protocol-test");
            startInfo.ArgumentList.Add("--bins");
            startInfo.Environment["CARGO_TARGET_DIR"] = targetRoot;

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("Failed to start Cargo while building the pinned ACP Testy binaries.");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                throw new TimeoutException("Building the pinned ACP Testy binaries exceeded the 15-minute limit.");
            }
            var output = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(testyPath) || !File.Exists(mcpEchoPath))
            {
                throw new InvalidOperationException(
                    $"Building ACP Testy at {RustSdkCommit} failed (exit code {process.ExitCode}).{Environment.NewLine}{output}{Environment.NewLine}{error}");
            }

            return new TestyBinaries(testyPath, mcpEchoPath);
        }
        finally
        {
            BuildLock.Release();
        }
    }

    private static async Task DownloadSourceAsync(string cacheRoot, string sourceRoot)
    {
        var archivePath = Path.Combine(cacheRoot, "rust-sdk-" + Guid.NewGuid().ToString("N") + ".zip");
        var extractRoot = Path.Combine(cacheRoot, "extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            var url = $"https://codeload.github.com/agentclientprotocol/rust-sdk/zip/{RustSdkCommit}";
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await using (var archive = await HttpClient.GetStreamAsync(url, timeout.Token).ConfigureAwait(false))
            await using (var file = File.Create(archivePath))
            {
                await archive.CopyToAsync(file, timeout.Token).ConfigureAwait(false);
            }

            Directory.CreateDirectory(extractRoot);
            ZipFile.ExtractToDirectory(archivePath, extractRoot);
            var extractedRoot = Directory.EnumerateDirectories(extractRoot).SingleOrDefault(directory => File.Exists(Path.Combine(directory, "Cargo.toml")))
                ?? throw new InvalidDataException("The pinned ACP Rust SDK archive did not contain its workspace Cargo.toml.");

            if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, recursive: true);
            Directory.Move(extractedRoot, sourceRoot);
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, recursive: true);
        }
    }

    private static async Task<FileStream> AcquireCacheLockAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var timeout = DateTime.UtcNow + TimeSpan.FromMinutes(15);
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < timeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
        }
    }

    private static bool CargoIsAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cargo", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process is null) return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string? ExistingFile(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? Path.GetFullPath(path) : null;
}
