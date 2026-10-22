using System.Text;
using System.Text.Json;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

/// <summary>
/// Security coverage for the client side of the Agent Client Protocol.
/// </summary>
/// <remarks>
/// The agent is the untrusted peer in an ACP session: it chooses the session ids, asks the client
/// to run commands, and asks the client to read and write files. These tests pin the boundaries
/// the client is responsible for enforcing.
/// </remarks>
public sealed class ClientSecurityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task McpStdioCommandIsRejectedUnlessTheSecurityPolicyAllowsIt()
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport, new ClientOptions
        {
            SecurityPolicy = new AcpSecurityPolicy { McpCommandAllowed = static _ => false }
        });

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => client.CreateSessionAsync(Options(new McpServerStdio
            {
                Name = "local",
                Command = Path.Combine(Path.GetTempPath(), "attacker.exe"),
                Args = [],
                Env = []
            }), Token));

        Assert.Contains("security policy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpStdioCommandIsAcceptedWhenTheSecurityPolicyAllowsIt()
    {
        await using var transport = new TestTransport();
        var command = Path.Combine(Path.GetTempPath(), "allowed.exe");
        await using var client = await ConnectAsync(transport, new ClientOptions
        {
            SecurityPolicy = new AcpSecurityPolicy { McpCommandAllowed = candidate => candidate == command }
        });

        var creating = client.CreateSessionAsync(Options(new McpServerStdio
        {
            Name = "local",
            Command = command,
            Args = [],
            Env = []
        }), Token);
        using (var request = await transport.ReadSentAsync())
        {
            transport.Respond(request, """{"sessionId":"session-1"}""");
        }

        var session = await creating.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal("session-1", session.Id);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.test/mcp")]
    [InlineData("gopher://example.test/")]
    public async Task McpEndpointWithANonHttpSchemeIsRejected(string url)
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport, new ClientOptions
        {
            SecurityPolicy = new AcpSecurityPolicy { McpEndpointAllowed = static _ => true }
        }, """{"protocolVersion":1,"agentCapabilities":{"mcpCapabilities":{"http":true,"sse":true}}}""");

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => client.CreateSessionAsync(Options(new McpServerHttp { Name = "remote", Url = url, Headers = [] }), Token));

        Assert.Contains("scheme", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("https://localhost/mcp")]
    [InlineData("http://10.0.0.5/mcp")]
    [InlineData("http://192.168.1.1/mcp")]
    public async Task McpEndpointAtARestrictedAddressIsRejectedByThePublicHttpsPolicy(string url)
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport, new ClientOptions
        {
            SecurityPolicy = AcpSecurityPolicy.AllowPublicHttpsMcpEndpoints()
        }, """{"protocolVersion":1,"agentCapabilities":{"mcpCapabilities":{"http":true,"sse":true}}}""");

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => client.CreateSessionAsync(Options(new McpServerHttp { Name = "remote", Url = url, Headers = [] }), Token));

        Assert.Contains("security policy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpEndpointIsRejectedByTheDefaultPolicy()
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport, new ClientOptions(), """{"protocolVersion":1,"agentCapabilities":{"mcpCapabilities":{"http":true}}}""");

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => client.CreateSessionAsync(Options(new McpServerHttp { Name = "remote", Url = "https://example.test/mcp", Headers = [] }), Token));

        Assert.Contains("security policy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TerminalCommandIsRejectedUnlessTheSecurityPolicyAllowsIt()
    {
        await using var transport = new TestTransport();
        var handler = new RecordingTerminalHandler();
        await using var client = await ConnectAsync(transport, new ClientOptions
        {
            TerminalHandler = handler,
            SecurityPolicy = new AcpSecurityPolicy { TerminalCommandAllowed = static (_, _) => false }
        });
        await CreateSessionAsync(transport, client, "session-1");

        transport.Receive(Request("create", "terminal/create", """{"sessionId":"session-1","command":"rm","args":["-rf","/"]}"""));
        using (var response = await transport.ReadSentAsync())
        {
            Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }

        Assert.Equal(0, handler.CreateCount);
    }

    [Fact]
    public async Task TerminalRequestForAnUnknownSessionIsRejectedBeforeTheHandlerRuns()
    {
        await using var transport = new TestTransport();
        var handler = new RecordingTerminalHandler();
        await using var client = await ConnectAsync(transport, new ClientOptions
        {
            TerminalHandler = handler,
            SecurityPolicy = new AcpSecurityPolicy { TerminalCommandAllowed = static (_, _) => true }
        });

        transport.Receive(Request("create", "terminal/create", """{"sessionId":"not-a-session","command":"echo"}"""));
        using (var response = await transport.ReadSentAsync())
        {
            Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }

        transport.Receive(Request("kill", "terminal/kill", """{"sessionId":"not-a-session","terminalId":"terminal-1"}"""));
        using (var response = await transport.ReadSentAsync())
        {
            Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }

        Assert.Equal(0, handler.CreateCount);
        Assert.Equal(0, handler.KillCount);
    }

    [Fact]
    public async Task TerminalOutputIsTruncatedToTheRequestedByteLimit()
    {
        await using var transport = new TestTransport();
        var handler = new RecordingTerminalHandler();
        await using var client = await ConnectAsync(transport, new ClientOptions
        {
            TerminalHandler = handler,
            SecurityPolicy = new AcpSecurityPolicy { TerminalCommandAllowed = static (_, _) => true }
        });
        await CreateSessionAsync(transport, client, "session-1");

        transport.Receive(Request("create", "terminal/create", """{"sessionId":"session-1","command":"echo","outputByteLimit":8}"""));
        using (var response = await transport.ReadSentAsync())
        {
            Assert.Equal("terminal-1", response.RootElement.GetProperty("result").GetProperty("terminalId").GetString());
        }

        transport.Receive(Request("output", "terminal/output", """{"sessionId":"session-1","terminalId":"terminal-1"}"""));
        using (var response = await transport.ReadSentAsync())
        {
            var result = response.RootElement.GetProperty("result");
            Assert.True(result.GetProperty("truncated").GetBoolean());
            var output = result.GetProperty("output").GetString()!;
            Assert.True(Encoding.UTF8.GetByteCount(output) <= 8, $"Retained {Encoding.UTF8.GetByteCount(output)} bytes.");
        }
    }

    [Fact]
    public async Task TerminalOutputTruncationLandsOnACharacterBoundary()
    {
        await using var transport = new TestTransport();
        var handler = new RecordingTerminalHandler();
        await using var client = await ConnectAsync(transport, new ClientOptions
        {
            TerminalHandler = handler,
            SecurityPolicy = new AcpSecurityPolicy { TerminalCommandAllowed = static (_, _) => true }
        });
        await CreateSessionAsync(transport, client, "session-1");

        // Four bytes of budget, but the handler returns multi-byte characters, so the retained
        // text must be shorter than the limit rather than split mid-sequence.
        transport.Receive(Request("create", "terminal/create", """{"sessionId":"session-1","command":"echo","outputByteLimit":4}"""));
        using (var response = await transport.ReadSentAsync())
        {
            Assert.Equal("terminal-1", response.RootElement.GetProperty("result").GetProperty("terminalId").GetString());
        }

        transport.Receive(Request("output", "terminal/output", """{"sessionId":"session-1","terminalId":"terminal-1"}"""));
        using (var response = await transport.ReadSentAsync())
        {
            var output = response.RootElement.GetProperty("result").GetProperty("output").GetString()!;
            Assert.True(Encoding.UTF8.GetByteCount(output) <= 4);
            // The retained text must still be valid UTF-8, which it is because the JSON parsed.
            Assert.DoesNotContain('�', output);
        }
    }

    [Fact]
    public async Task FileSystemHandlerIsGivenTheResolvedPathNotTheAgentSuppliedOne()
    {
        var workspace = Directory.CreateTempSubdirectory("acp-workspace-");
        var outside = Directory.CreateTempSubdirectory("acp-outside-");
        try
        {
            await using var transport = new TestTransport();
            var handler = new RecordingFileSystemHandler();
            await using var client = await ConnectAsync(transport, new ClientOptions { FileSystemHandler = handler });
            await CreateSessionAsync(transport, client, "session-1", workspace.FullName);

            var target = Path.Combine(workspace.FullName, "..", Path.GetFileName(outside.FullName), "secret.txt");
            transport.Receive(FileSystemRequest("read", "fs/read_text_file", "session-1", target));
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

    [Fact]
    public async Task SessionCountIsCapped()
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport, new ClientOptions { MaximumSessions = 2 });

        for (var index = 0; index < 2; index++)
        {
            var creating = client.CreateSessionAsync(Options(), Token);
            using (var request = await transport.ReadSentAsync())
            {
                transport.Respond(request, $$"""{"sessionId":"session-{{index}}"}""");
            }
            _ = await creating.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }

        var overflow = client.CreateSessionAsync(Options(), Token);
        using (var request = await transport.ReadSentAsync())
        {
            transport.Respond(request, """{"sessionId":"session-overflow"}""");
        }

        var exception = await Assert.ThrowsAsync<ProtocolException>(
            () => overflow.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Contains("live sessions", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadingASessionThatIsAlreadyLiveReplacesItsWorkspaceRoots()
    {
        // The ACP session setup flow allows a load to address a live session. The loaded session
        // must not keep the workspace roots of the session it was first created with, because the
        // caller asked for a different workspace.
        var first = Directory.CreateTempSubdirectory("acp-first-");
        var second = Directory.CreateTempSubdirectory("acp-second-");
        try
        {
            await using var transport = new TestTransport();
            var handler = new RecordingFileSystemHandler();
            await using var client = await ConnectAsync(
                transport,
                new ClientOptions { FileSystemHandler = handler },
                """{"protocolVersion":1,"agentCapabilities":{"loadSession":true}}""");
            await CreateSessionAsync(transport, client, "session-1", first.FullName);

            var loading = client.LoadSessionAsync(
                "session-1",
                new SessionOptions { WorkingDirectory = second.FullName },
                Token);
            using (var request = await transport.ReadSentAsync())
            {
                transport.Respond(request, """{"sessionId":"session-1"}""");
            }
            _ = await loading.WaitAsync(TimeSpan.FromSeconds(5), Token);

            // A file under the second workspace is now allowed, because the roots were replaced.
            var allowed = Path.Combine(second.FullName, "allowed.txt");
            transport.Receive(FileSystemRequest("read", "fs/read_text_file", "session-1", allowed));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.Equal("read", response.RootElement.GetProperty("id").GetString());
                Assert.True(response.RootElement.TryGetProperty("result", out _));
            }

            // A file under the first workspace is no longer allowed.
            var rejected = Path.Combine(first.FullName, "denied.txt");
            transport.Receive(FileSystemRequest("denied", "fs/read_text_file", "session-1", rejected));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            }
        }
        finally
        {
            first.Delete(recursive: true);
            second.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://localhost/")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://10.1.2.3/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://192.168.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[fe80::1]/")]
    public void RestrictedAddressesAreDetected(string url)
    {
        Assert.True(AcpSecurityPolicy.IsRestrictedAddress(new Uri(url)));
    }

    [Theory]
    [InlineData("https://example.test/mcp")]
    [InlineData("https://8.8.8.8/mcp")]
    public void PublicAddressesAreAllowed(string url)
    {
        Assert.False(AcpSecurityPolicy.IsRestrictedAddress(new Uri(url)));
    }

    private static SessionOptions Options() => new() { WorkingDirectory = Path.GetTempPath() };

    private static SessionOptions Options(McpServer server) => new()
    {
        WorkingDirectory = Path.GetTempPath(),
        McpServers = [server]
    };

    private static async Task<AcpClient> ConnectAsync(TestTransport transport, ClientOptions options, string agentResponse = """{"protocolVersion":1,"agentCapabilities":{}}""")
    {
        var connecting = AcpClient.ConnectAsync(transport, options, Token);
        using var initialize = await transport.ReadSentAsync();
        transport.Respond(initialize, agentResponse);
        return await connecting.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    private static async Task CreateSessionAsync(TestTransport transport, AcpClient client, string sessionId, string? workingDirectory = null)
    {
        var creating = client.CreateSessionAsync(
            new SessionOptions { WorkingDirectory = workingDirectory ?? Path.GetTempPath() },
            Token);
        using var request = await transport.ReadSentAsync();
        transport.Respond(request, $$"""{"sessionId":"{{sessionId}}"}""");
        _ = await creating.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    private static string Request(string id, string method, string parameters) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + QuoteJsonString(id) + ",\"method\":" + QuoteJsonString(method) + ",\"params\":" + parameters + "}";

    private static string FileSystemRequest(string id, string method, string sessionId, string path) =>
        Request(id, method, "{\"sessionId\":" + QuoteJsonString(sessionId) + ",\"path\":" + QuoteJsonString(path) + "}");

    private static string QuoteJsonString(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";

    private sealed class RecordingTerminalHandler : ITerminalHandler
    {
        private int _createCount;
        private int _killCount;

        public int CreateCount => Volatile.Read(ref _createCount);
        public int KillCount => Volatile.Read(ref _killCount);

        public ValueTask<string> CreateAsync(CreateTerminalRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _createCount);
            return ValueTask.FromResult("terminal-1");
        }

        public ValueTask<TerminalOutputResponse> GetOutputAsync(TerminalOutputRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new TerminalOutputResponse
            {
                Output = new string('a', 64),
                Truncated = false
            });

        public ValueTask<TerminalExitStatus> WaitForExitAsync(WaitForTerminalExitRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new TerminalExitStatus { ExitCode = 0 });

        public ValueTask KillAsync(KillTerminalRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _killCount);
            return ValueTask.CompletedTask;
        }

        public ValueTask ReleaseAsync(ReleaseTerminalRequest request, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class RecordingFileSystemHandler : IFileSystemHandler
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public ValueTask<string> ReadTextFileAsync(ReadTextFileRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCount);
            return ValueTask.FromResult(request.Path);
        }

        public ValueTask WriteTextFileAsync(WriteTextFileRequest request, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
