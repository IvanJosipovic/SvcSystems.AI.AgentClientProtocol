using System.IO.Compression;
using System.Formats.Tar;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SvcSystems.AI.AgentClientProtocol.Registry;
using SvcSystems.AI.AgentClientProtocol.Registry.Models;
using SvcSystems.AI.AgentClientProtocol.Registry.Serialization;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

public sealed class RegistryAgentLauncherTests
{
    [Fact]
    public async Task PrefersNpxAndPassesPinnedPackageArgumentsAndEnvironment()
    {
        var agent = CreateAgent("npm-agent", "1.2.3", npx: new packageDistribution
        {
            Package = "@scope/agent@1.2.3",
            Args = ["--acp", "two words"],
            Env = new Dictionary<string, string> { ["AGENT_MODE"] = "test" },
        }, uvx: new packageDistribution { Package = "fallback==1.2.3" });
        using var launcher = CreateLauncher((command, _) => Task.FromResult<string?>(command == NpxCommand ? "fake-npx" : "fake-uvx"));

        var process = await launcher.BuildProcessOptionsAsync(agent, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("fake-npx", process.FileName);
        Assert.Equal(["--yes", "@scope/agent@1.2.3", "--acp", "two words"], process.Arguments);
        Assert.Equal("test", process.Environment["AGENT_MODE"]);
    }

    [Fact]
    public async Task ChecksPackageRunnerAvailabilityBeforeBuildingOptions()
    {
        using var launcher = new RegistryAgentLauncher();
        var agent = CreateAgent("npm-agent", "1.2.3", npx: new packageDistribution { Package = "agent@1.2.3" });
        ProcessOptions process;
        try
        {
            process = await launcher.BuildProcessOptionsAsync(agent, cancellationToken: TestContext.Current.CancellationToken);
        }
        catch (RegistryAgentLaunchException exception) when (exception.Message.Contains("npx command is missing", StringComparison.Ordinal))
        {
            Assert.Skip("npx is not installed on this test host.");
            return;
        }

        Assert.True(File.Exists(process.FileName));
        Assert.Equal(["--yes", "agent@1.2.3"], process.Arguments);
    }

    [Fact]
    public async Task FallsBackToUvxWhenNpxIsUnavailableAndPreservesPipVersionSyntax()
    {
        var agent = CreateAgent("uv-agent", "0.10.1",
            npx: new packageDistribution { Package = "unused@1.0.0" },
            uvx: new packageDistribution { Package = "fast-agent-acp==0.10.1", Args = ["-x"] });
        using var launcher = CreateLauncher((command, _) =>
            Task.FromResult<string?>(command == "uvx" ? "fake-uvx" : null));

        var process = await launcher.BuildProcessOptionsAsync(agent, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("fake-uvx", process.FileName);
        Assert.Equal(["fast-agent-acp==0.10.1", "-x"], process.Arguments);
    }

    [Fact]
    public async Task PreviewChannelUsesItsPinnedNpxDistributionWhenSelected()
    {
        var agent = CreateAgent(
            "preview-agent",
            "1.0.0",
            npx: new packageDistribution { Package = "preview-agent@1.0.0" },
            preview: new previewChannel
            {
                Version = "1.1.0-preview.2",
                Distribution = new previewChannelDistribution
                {
                    Npx = new packageDistribution { Package = "preview-agent@1.1.0-preview.2", Args = ["--acp"] },
                },
            });
        using var launcher = CreateLauncher(
            (_, _) => Task.FromResult<string?>("fake-npx"),
            new RegistryAgentLauncherOptions { ReleaseChannel = RegistryAgentReleaseChannel.Preview });

        var process = await launcher.BuildProcessOptionsAsync(agent, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["--yes", "preview-agent@1.1.0-preview.2", "--acp"], process.Arguments);
    }

    [Fact]
    public async Task PreviewBinaryUsesPreviewVersionInItsCachePath()
    {
        var platform = RegistryAgentLauncher.GetRegistryPlatform();
        Assert.NotNull(platform);
        var zipBytes = CreateZip("agent.exe", "preview executable");
        var agent = CreateAgent(
            "preview-binary-agent",
            "1.0.0",
            npx: new packageDistribution { Package = "preview-binary-agent@1.0.0" },
            preview: new previewChannel
            {
                Version = "1.1.0-preview.3",
                Distribution = new previewChannelDistribution
                {
                    Binary = CreateBinaryDistribution(platform.Value.RegistryName, "https://registry.test/preview.zip", "agent.exe"),
                },
            });
        var agentsDirectory = Path.Combine(Path.GetTempPath(), $"acp-preview-agents-{Guid.NewGuid():N}");
        using var httpClient = new HttpClient(new StaticResponseHandler(zipBytes));
        using var launcher = new RegistryAgentLauncher(
            httpClient,
            new RegistryAgentLauncherOptions { AgentsDirectory = agentsDirectory, ReleaseChannel = RegistryAgentReleaseChannel.Preview },
            (_, _) => Task.FromResult<string?>(null));

        try
        {
            var process = await launcher.BuildProcessOptionsAsync(agent, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains(Path.Combine("preview-binary-agent", "1.1.0-preview.3", platform.Value.RuntimeIdentifier), process.FileName, StringComparison.Ordinal);
            Assert.Equal("preview executable", await File.ReadAllTextAsync(process.FileName, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(agentsDirectory))
            {
                Directory.Delete(agentsDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PreviewSelectionDoesNotSilentlyFallBackToStable()
    {
        var agent = CreateAgent("stable-only", "1.0.0", npx: new packageDistribution { Package = "stable-only@1.0.0" });
        using var launcher = CreateLauncher(
            (_, _) => Task.FromResult<string?>("fake-npx"),
            new RegistryAgentLauncherOptions { ReleaseChannel = RegistryAgentReleaseChannel.Preview });

        var exception = await Assert.ThrowsAsync<RegistryAgentLaunchException>(
            () => launcher.BuildProcessOptionsAsync(agent, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("does not advertise a preview", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FallsBackToBinaryWhenPackageManagersAreUnavailableAndVerifiesArchiveChecksum()
    {
        var platform = RegistryAgentLauncher.GetRegistryPlatform();
        Assert.NotNull(platform);
        var zipBytes = CreateZip("agent.exe", "test executable");
        var sha256 = Convert.ToHexString(SHA256.HashData(zipBytes));
        var agent = CreateAgent("binary-agent", "1.2.3", binary: new binaryDistribution
        {
            Targets = new Dictionary<string, binaryTarget>
            {
                [platform.Value.RegistryName] = new binaryTarget
                {
                    Archive = "https://registry.test/agent.zip",
                    Sha256 = sha256,
                    Cmd = "agent.exe",
                    Args = ["acp"],
                },
            },
        });
        var agentsDirectory = Path.Combine(Path.GetTempPath(), $"acp-agents-{Guid.NewGuid():N}");
        using var httpClient = new HttpClient(new StaticResponseHandler(zipBytes));
        using var launcher = new RegistryAgentLauncher(
            httpClient,
            new RegistryAgentLauncherOptions { AgentsDirectory = agentsDirectory },
            (_, _) => Task.FromResult<string?>(null));

        try
        {
            var process = await launcher.BuildProcessOptionsAsync(agent, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(["acp"], process.Arguments);
            Assert.EndsWith(Path.Combine("binary-agent", "1.2.3", platform.Value.RuntimeIdentifier, "agent.exe"), process.FileName);
            Assert.Equal("test executable", await File.ReadAllTextAsync(process.FileName, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(agentsDirectory))
            {
                Directory.Delete(agentsDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RejectsBinaryArchiveWhenChecksumDoesNotMatch()
    {
        var platform = RegistryAgentLauncher.GetRegistryPlatform();
        Assert.NotNull(platform);
        var zipBytes = CreateZip("agent.exe", "test executable");
        var agent = CreateAgent("binary-agent", "1.2.3", binary: new binaryDistribution
        {
            Targets = new Dictionary<string, binaryTarget>
            {
                [platform.Value.RegistryName] = new binaryTarget
                {
                    Archive = "https://registry.test/agent.zip",
                    Sha256 = new string('0', 64),
                    Cmd = "agent.exe",
                },
            },
        });
        var agentsDirectory = Path.Combine(Path.GetTempPath(), $"acp-agents-{Guid.NewGuid():N}");
        using var httpClient = new HttpClient(new StaticResponseHandler(zipBytes));
        using var launcher = new RegistryAgentLauncher(
            httpClient,
            new RegistryAgentLauncherOptions { AgentsDirectory = agentsDirectory },
            (_, _) => Task.FromResult<string?>(null));

        try
        {
            var exception = await Assert.ThrowsAsync<RegistryAgentLaunchException>(
                () => launcher.BuildProcessOptionsAsync(agent, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("SHA-256 verification failed", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(agentsDirectory))
            {
                Directory.Delete(agentsDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExtractsGZipTarArchiveIntoWorkingDirectoryAgentCache()
    {
        var platform = RegistryAgentLauncher.GetRegistryPlatform();
        Assert.NotNull(platform);
        var archiveBytes = CreateTarGZip("bundle/agent", "tar executable");
        var agent = CreateAgent("tar-agent", "2.0.0", binary: CreateBinaryDistribution(
            platform.Value.RegistryName,
            "https://registry.test/agent.tar.gz",
            "bundle/agent"));
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"acp-working-{Guid.NewGuid():N}");
        using var httpClient = new HttpClient(new StaticResponseHandler(archiveBytes));
        using var launcher = new RegistryAgentLauncher(httpClient, null, (_, _) => Task.FromResult<string?>(null));

        try
        {
            var process = await launcher.BuildProcessOptionsAsync(agent, workingDirectory, TestContext.Current.CancellationToken);

            Assert.Equal(workingDirectory, process.WorkingDirectory);
            Assert.Equal("tar executable", await File.ReadAllTextAsync(process.FileName, TestContext.Current.CancellationToken));
            Assert.StartsWith(Path.Combine(workingDirectory, "agents", "tar-agent"), process.FileName, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CopiesRawBinaryToTheConfiguredAgentCache()
    {
        var platform = RegistryAgentLauncher.GetRegistryPlatform();
        Assert.NotNull(platform);
        var agent = CreateAgent("raw-agent", "1.0.0", binary: CreateBinaryDistribution(
            platform.Value.RegistryName,
            "https://registry.test/agent.bin",
            "agent.bin"));
        var agentsDirectory = Path.Combine(Path.GetTempPath(), $"acp-agents-{Guid.NewGuid():N}");
        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.UTF8.GetBytes("raw executable")));
        using var launcher = new RegistryAgentLauncher(
            httpClient,
            new RegistryAgentLauncherOptions { AgentsDirectory = agentsDirectory },
            (_, _) => Task.FromResult<string?>(null));

        try
        {
            var process = await launcher.BuildProcessOptionsAsync(agent, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("raw executable", await File.ReadAllTextAsync(process.FileName, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(agentsDirectory))
            {
                Directory.Delete(agentsDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExtractsBZip2TarArchiveWhenSystemTarIsAvailable()
    {
        var platform = RegistryAgentLauncher.GetRegistryPlatform();
        Assert.NotNull(platform);
        var archiveBytes = await CreateBZip2TarOrSkipAsync(TestContext.Current.CancellationToken);
        var agent = CreateAgent("bzip-agent", "1.0.0", binary: CreateBinaryDistribution(
            platform.Value.RegistryName,
            "https://registry.test/agent.tar.bz2",
            "agent"));
        var agentsDirectory = Path.Combine(Path.GetTempPath(), $"acp-agents-{Guid.NewGuid():N}");
        using var httpClient = new HttpClient(new StaticResponseHandler(archiveBytes));
        using var launcher = new RegistryAgentLauncher(
            httpClient,
            new RegistryAgentLauncherOptions { AgentsDirectory = agentsDirectory },
            (command, _) => Task.FromResult<string?>(command == "tar" ? "tar" : null));

        try
        {
            var process = await launcher.BuildProcessOptionsAsync(agent, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("bzip executable", await File.ReadAllTextAsync(process.FileName, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(agentsDirectory))
            {
                Directory.Delete(agentsDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExplainsMissingPackageManagersAndUnsupportedBinaryTarget()
    {
        var agent = CreateAgent("unsupported", "1.0.0",
            npx: new packageDistribution { Package = "agent@1.0.0" },
            uvx: new packageDistribution { Package = "agent==1.0.0" },
            binary: new binaryDistribution());
        using var launcher = CreateLauncher((_, _) => Task.FromResult<string?>(null));

        var exception = await Assert.ThrowsAsync<RegistryAgentLaunchException>(() =>
            launcher.BuildProcessOptionsAsync(agent, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("npx command is missing", exception.Message, StringComparison.Ordinal);
        Assert.Contains("uvx command is missing", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("darwin", Architecture.Arm64, "darwin-aarch64", "osx-arm64")]
    [InlineData("darwin", Architecture.X64, "darwin-x86_64", "osx-x64")]
    [InlineData("linux", Architecture.Arm64, "linux-aarch64", "linux-arm64")]
    [InlineData("linux", Architecture.X64, "linux-x86_64", "linux-x64")]
    [InlineData("windows", Architecture.Arm64, "windows-aarch64", "win-arm64")]
    [InlineData("windows", Architecture.X64, "windows-x86_64", "win-x64")]
    public void MapsRegistryBinaryPlatformNamesToDotnetRuntimeIdentifiers(
        string operatingSystem,
        Architecture architecture,
        string registryName,
        string runtimeIdentifier)
    {
        var platform = RegistryAgentLauncher.GetRegistryPlatform(operatingSystem, architecture);
        Assert.NotNull(platform);
        Assert.Equal(registryName, platform.Value.RegistryName);
        Assert.Equal(runtimeIdentifier, platform.Value.RuntimeIdentifier);
    }

    private static string NpxCommand => OperatingSystem.IsWindows() ? "npx.cmd" : "npx";

    private static RegistryAgentLauncher CreateLauncher(
        Func<string, CancellationToken, Task<string?>> findCommand,
        RegistryAgentLauncherOptions? options = null) =>
        new(null, new RegistryAgentLauncherOptions
        {
            AgentsDirectory = options?.AgentsDirectory ?? Path.Combine(Path.GetTempPath(), $"acp-agents-{Guid.NewGuid():N}"),
            ReleaseChannel = options?.ReleaseChannel ?? RegistryAgentReleaseChannel.Stable,
        }, findCommand);

    private static RegistryAgent CreateAgent(
        string id,
        string version,
        packageDistribution? npx = null,
        packageDistribution? uvx = null,
        binaryDistribution? binary = null,
        previewChannel? preview = null) => new()
    {
        Id = id,
        Name = id,
        Version = version,
        Description = id,
        Distribution = new RegistryAgentDistribution { Npx = npx, Uvx = uvx, Binary = binary },
        Preview = preview,
    };

    private static binaryDistribution CreateBinaryDistribution(string platform, string archive, string command) => new()
    {
        Targets = new Dictionary<string, binaryTarget>
        {
            [platform] = new binaryTarget
            {
                Archive = archive,
                Cmd = command,
            },
        },
    };

    private static byte[] CreateZip(string fileName, string content)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry(fileName).Open()))
        {
            writer.Write(content);
        }

        return buffer.ToArray();
    }

    private static byte[] CreateTarGZip(string fileName, string content)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, fileName)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
            });
        }

        return buffer.ToArray();
    }

    private static async Task<byte[]> CreateBZip2TarOrSkipAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), $"acp-tar-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(root, "source");
        var archivePath = Path.Combine(root, "archive.tar.bz2");
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "agent"), "bzip executable", cancellationToken);
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "tar",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                },
            };
            foreach (var argument in new[] { "-cjf", archivePath, "-C", sourceDirectory, "agent" })
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            try
            {
                if (!process.Start())
                {
                    Assert.Skip("The system tar command is unavailable.");
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Assert.Skip("The system tar command is unavailable.");
            }

            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                Assert.Skip("The system tar command does not support bzip2 archives.");
            }

            return await File.ReadAllBytesAsync(archivePath, cancellationToken);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class StaticResponseHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            });
    }
}
