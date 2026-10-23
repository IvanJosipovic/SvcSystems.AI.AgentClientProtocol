using System.IO.Compression;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using SvcSystems.AI.AgentClientProtocol.Registry;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

public sealed class RegistryArchiveSecurityTests
{
    private static readonly string TestAgentsDirectory = Path.Combine(Path.GetTempPath(), "acp-test-archives-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ZipArchiveWithSymlinkIsRejected()
    {
        var archivePath = await CreateZipWithSymlink();
        var installer = CreateInstaller();
        var ex = Assert.Throws<RegistryAgentLaunchException>(() => installer.ExtractZipAsync(archivePath, TestAgentsDirectory, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Contains("symbolic links", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TarGzArchiveWithSymlinkIsRejected()
    {
        var archivePath = await CreateTarGzWithSymlink();
        var installer = CreateInstaller();
        var ex = Assert.Throws<RegistryAgentLaunchException>(() => installer.ExtractGZipTarAsync(archivePath, TestAgentsDirectory, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Contains("links", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TarGzArchiveWithHardlinkIsRejected()
    {
        var archivePath = await CreateTarGzWithHardlink();
        var installer = CreateInstaller();
        var ex = Assert.Throws<RegistryAgentLaunchException>(() => installer.ExtractGZipTarAsync(archivePath, TestAgentsDirectory, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Contains("links", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ZipArchivePathTraversalIsRejected()
    {
        var archivePath = await CreateZipWithPathTraversal();
        var installer = CreateInstaller();
        var ex = Assert.Throws<RegistryAgentLaunchException>(() => installer.ExtractZipAsync(archivePath, TestAgentsDirectory, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Contains("escapes configured agent directory", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ArchiveExceedingMaxEntriesIsRejected()
    {
        var archivePath = await CreateZipWithManyEntries(10_001);
        var installer = CreateInstaller(maxArchiveEntries: 10_000);
        var ex = Assert.Throws<RegistryAgentLaunchException>(() => installer.ExtractZipAsync(archivePath, TestAgentsDirectory, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Contains("entry count exceeds maximum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ArchiveExceedingMaxBytesIsRejected()
    {
        var archivePath = await CreateZipWithLargeContent(200_000_000); // 200 MB
        var installer = CreateInstaller(maxArchiveBytes: 100_000_000); // 100 MB limit
        var ex = Assert.Throws<RegistryAgentLaunchException>(() => installer.ExtractZipAsync(archivePath, TestAgentsDirectory, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Contains("exceeds maximum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ArchiveExceedingCompressionRatioIsRejected()
    {
        // Create a zip with 10MB of zeros that compresses to ~100KB (ratio ~100)
        var archivePath = await CreateZipWithHighCompressionRatio(100);
        var installer = CreateInstaller(maxCompressionRatio: 50.0);
        var ex = Assert.Throws<RegistryAgentLaunchException>(() => installer.ExtractZipAsync(archivePath, TestAgentsDirectory, CancellationToken.None).GetAwaiter().GetResult());
        Assert.Contains("compression ratio", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RawBinaryExceedingMaxBytesIsRejected()
    {
        var archivePath = CreateRawBinary(200_000_000); // 200 MB
        var installer = CreateInstaller(maxArchiveBytes: 100_000_000);
        var ex = Assert.Throws<RegistryAgentLaunchException>(() => installer.ExtractArchiveAsync(new Uri("file://" + archivePath), archivePath, TestAgentsDirectory, "agent", CancellationToken.None).GetAwaiter().GetResult());
        Assert.Contains("exceeds maximum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private RegistryBinaryInstaller CreateInstaller(
        long maxArchiveBytes = 100_000_000,
        int maxArchiveEntries = 10_000,
        double maxCompressionRatio = 100.0) =>
        new RegistryBinaryInstaller(
            new HttpClient(),
            TestAgentsDirectory,
            static (_, _) => Task.FromResult<string?>(null),
            requireChecksum: false,
            maxArchiveBytes,
            maxArchiveEntries,
            maxCompressionRatio);

    private async Task<string> CreateZipWithSymlink()
    {
        var path = Path.Combine(TestAgentsDirectory, "symlink.zip");
        Directory.CreateDirectory(TestAgentsDirectory);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("link");
        entry.ExternalAttributes = (0xA000 << 16) | 0x1FF; // Symlink (0o777 = 0x1FF)
        await using var stream = entry.Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("target"));
        return path;
    }

    private async Task<string> CreateTarGzWithSymlink()
    {
        var path = Path.Combine(TestAgentsDirectory, "symlink.tar.gz");
        Directory.CreateDirectory(TestAgentsDirectory);
        await using var output = File.Create(path);
        await using var gzip = new GZipStream(output, CompressionMode.Compress);
        using var writer = new TarWriter(gzip);
        var entry = new PaxTarEntry(TarEntryType.SymbolicLink, "link")
        {
            LinkName = "target"
        };
        writer.WriteEntry(entry);
        return path;
    }

    private async Task<string> CreateTarGzWithHardlink()
    {
        var path = Path.Combine(TestAgentsDirectory, "hardlink.tar.gz");
        Directory.CreateDirectory(TestAgentsDirectory);
        await using var output = File.Create(path);
        await using var gzip = new GZipStream(output, CompressionMode.Compress);
        using var writer = new TarWriter(gzip);
        var entry = new PaxTarEntry(TarEntryType.HardLink, "link")
        {
            LinkName = "target"
        };
        writer.WriteEntry(entry);
        return path;
    }

    private async Task<string> CreateZipWithPathTraversal()
    {
        var path = Path.Combine(TestAgentsDirectory, "traversal.zip");
        Directory.CreateDirectory(TestAgentsDirectory);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("../outside.txt");
        await using var stream = entry.Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("malicious"));
        return path;
    }

    private async Task<string> CreateZipWithManyEntries(int count)
    {
        var path = Path.Combine(TestAgentsDirectory, $"many-entries-{count}.zip");
        Directory.CreateDirectory(TestAgentsDirectory);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        for (int i = 0; i < count; i++)
        {
            var entry = archive.CreateEntry($"file{i}.txt");
            await using var stream = entry.Open();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("content"));
        }
        return path;
    }

    private async Task<string> CreateZipWithLargeContent(long totalBytes)
    {
        var path = Path.Combine(TestAgentsDirectory, "large.zip");
        Directory.CreateDirectory(TestAgentsDirectory);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("large.bin");
        await using var stream = entry.Open();
        var buffer = new byte[1_000_000];
        long written = 0;
        while (written < totalBytes)
        {
            var toWrite = (int)Math.Min(buffer.Length, totalBytes - written);
            await stream.WriteAsync(buffer.AsMemory(0, toWrite));
            written += toWrite;
        }
        return path;
    }

    private async Task<string> CreateZipWithHighCompressionRatio(int ratio)
    {
        // Create content that compresses well (repeated zeros)
        var path = Path.Combine(TestAgentsDirectory, "high-ratio.zip");
        Directory.CreateDirectory(TestAgentsDirectory);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("zeros.bin");
        await using var stream = entry.Open();
        var zeros = new byte[10_000_000]; // 10 MB of zeros
        await stream.WriteAsync(zeros);
        return path;
    }

    private string CreateRawBinary(long size)
    {
        var path = Path.Combine(TestAgentsDirectory, "raw.bin");
        Directory.CreateDirectory(TestAgentsDirectory);
        using var stream = File.Create(path);
        var buffer = new byte[1_000_000];
        long written = 0;
        while (written < size)
        {
            var toWrite = (int)Math.Min(buffer.Length, size - written);
            stream.Write(buffer, 0, toWrite);
            written += toWrite;
        }
        return path;
    }
}