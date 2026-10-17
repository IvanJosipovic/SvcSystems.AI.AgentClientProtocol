using System.Text.Json;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

public sealed class FileSystemCallbackTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FileSystemCallbacksAreRestrictedToSessionWorkspaceRoots()
    {
        var workspace = Directory.CreateTempSubdirectory("acp-workspace-");
        var additional = Directory.CreateTempSubdirectory("acp-additional-");
        var outside = Directory.CreateTempSubdirectory("acp-outside-");
        try
        {
            await using var transport = new TestTransport();
            var handler = new RecordingFileSystemHandler();
            var connecting = AcpClient.ConnectAsync(transport, new ClientOptions { FileSystemHandler = handler }, Token);
            using (var initialize = await transport.ReadSentAsync())
            {
                transport.Respond(initialize, """{"protocolVersion":1,"agentCapabilities":{"sessionCapabilities":{"additionalDirectories":{}}}}""");
            }
            await using var client = await connecting.WaitAsync(TimeSpan.FromSeconds(5), Token);

            var creatingSession = client.CreateSessionAsync(new SessionOptions
            {
                WorkingDirectory = workspace.FullName,
                AdditionalDirectories = [additional.FullName]
            }, Token);
            using (var request = await transport.ReadSentAsync())
            {
                Assert.Equal("session/new", request.RootElement.GetProperty("method").GetString());
                transport.Respond(request, """{"sessionId":"session-1"}""");
            }
            _ = await creatingSession.WaitAsync(TimeSpan.FromSeconds(5), Token);

            var allowedPath = Path.Combine(additional.FullName, "allowed.txt");
            transport.Receive(FileSystemRequest("allowed-read", "fs/read_text_file", "session-1", allowedPath));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.Equal("allowed-read", response.RootElement.GetProperty("id").GetString());
                Assert.Equal(allowedPath, response.RootElement.GetProperty("result").GetProperty("content").GetString());
            }

            var traversalPath = Path.Combine(workspace.FullName, "..", Path.GetFileName(outside.FullName), "secret.txt");
            transport.Receive(FileSystemRequest("blocked-read", "fs/read_text_file", "session-1", traversalPath));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            }

            transport.Receive(FileSystemRequest("blocked-write", "fs/write_text_file", "session-1", Path.Combine(outside.FullName, "secret.txt")));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            }

            Assert.Equal(1, handler.ReadCount);
            Assert.Equal(0, handler.WriteCount);
        }
        finally
        {
            workspace.Delete(recursive: true);
            additional.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FileSystemCallbacksRejectSymlinkEscapes()
    {
        var workspace = Directory.CreateTempSubdirectory("acp-workspace-");
        var outside = Directory.CreateTempSubdirectory("acp-outside-");
        try
        {
            var linkPath = Path.Combine(workspace.FullName, "external");
            try
            {
                Directory.CreateSymbolicLink(linkPath, outside.FullName);
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                Assert.Skip("Creating symbolic links requires Developer Mode or symbolic-link privileges on Windows.");
            }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            {
                Assert.Skip("Creating symbolic links requires Developer Mode or symbolic-link privileges on Windows.");
            }

            await using var transport = new TestTransport();
            var handler = new RecordingFileSystemHandler();
            await using var client = await ConnectAsync(transport, new ClientOptions { FileSystemHandler = handler });

            var creatingSession = client.CreateSessionAsync(new SessionOptions { WorkingDirectory = workspace.FullName }, Token);
            using (var request = await transport.ReadSentAsync())
            {
                transport.Respond(request, """{"sessionId":"session-1"}""");
            }
            _ = await creatingSession.WaitAsync(TimeSpan.FromSeconds(5), Token);

            transport.Receive(FileSystemRequest("symlink-read", "fs/read_text_file", "session-1", Path.Combine(linkPath, "secret.txt")));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            }

            Assert.Equal(0, handler.ReadCount);
        }
        finally
        {
            workspace.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    private static async Task<AcpClient> ConnectAsync(TestTransport transport, ClientOptions? options = null)
    {
        var connecting = AcpClient.ConnectAsync(transport, options, Token);
        using var request = await transport.ReadSentAsync();
        transport.Respond(request, """{"protocolVersion":1,"agentCapabilities":{"loadSession":true,"sessionCapabilities":{"resume":{}}}}""");
        return await connecting.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    private static string FileSystemRequest(string id, string method, string sessionId, string path)
    {
        var parameters = method == "fs/write_text_file"
            ? "{\"sessionId\":" + QuoteJsonString(sessionId) + ",\"path\":" + QuoteJsonString(path) + ",\"content\":\"data\"}"
            : "{\"sessionId\":" + QuoteJsonString(sessionId) + ",\"path\":" + QuoteJsonString(path) + "}";
        return "{\"jsonrpc\":\"2.0\",\"id\":" + QuoteJsonString(id) + ",\"method\":" + QuoteJsonString(method) + ",\"params\":" + parameters + "}";
    }

    private static string QuoteJsonString(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";

    private sealed class RecordingFileSystemHandler : IFileSystemHandler
    {
        private int _readCount;
        private int _writeCount;

        public int ReadCount => Volatile.Read(ref _readCount);
        public int WriteCount => Volatile.Read(ref _writeCount);

        public ValueTask<string> ReadTextFileAsync(ReadTextFileRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCount);
            return ValueTask.FromResult(request.Path);
        }

        public ValueTask WriteTextFileAsync(WriteTextFileRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writeCount);
            return ValueTask.CompletedTask;
        }
    }
}
