using System.Text.Json;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

public sealed class TerminalCallbackTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TerminalCapabilityIsAdvertisedOnlyWhenAHandlerIsConfigured()
    {
        await using var bareTransport = new TestTransport();
        var (bare, bareCapabilities) = await ConnectAsync(bareTransport, new ClientOptions());
        await using (bare)
        {
            Assert.False(bareCapabilities.GetProperty("terminal").GetBoolean());

            bareTransport.Receive(Request("terminal", "terminal/create", """{"sessionId":"session-1","command":"echo"}"""));
            using var response = await bareTransport.ReadSentAsync();
            Assert.Equal(-32601, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }

        await using var hostTransport = new TestTransport();
        var (host, hostCapabilities) = await ConnectAsync(hostTransport, new ClientOptions { TerminalHandler = new RecordingTerminalHandler() });
        await using (host)
        {
            Assert.True(hostCapabilities.GetProperty("terminal").GetBoolean());
        }
    }

    [Fact]
    public async Task TerminalCallbacksAreRoutedToTheConfiguredHandler()
    {
        var cwd = Path.GetTempPath();
        await using var transport = new TestTransport();
        var handler = new RecordingTerminalHandler();
        var (client, _) = await ConnectAsync(transport, new ClientOptions
        {
            TerminalHandler = handler,
            SecurityPolicy = new AcpSecurityPolicy
            {
                TerminalCommandAllowed = static (_, _) => true
            }
        });
        await using (client)
        {
            await CreateSessionAsync(transport, client, "session-1");

            transport.Receive(Request(
                "create",
                "terminal/create",
                "{\"sessionId\":\"session-1\",\"command\":\"dotnet\",\"args\":[\"--version\"],\"cwd\":" + QuoteJsonString(cwd) + ",\"outputByteLimit\":4096}"));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.Equal("terminal-1", response.RootElement.GetProperty("result").GetProperty("terminalId").GetString());
            }

            transport.Receive(Request("output", "terminal/output", """{"sessionId":"session-1","terminalId":"terminal-1"}"""));
            using (var response = await transport.ReadSentAsync())
            {
                var result = response.RootElement.GetProperty("result");
                Assert.Equal("partial output", result.GetProperty("output").GetString());
                Assert.True(result.GetProperty("truncated").GetBoolean());
                Assert.Equal(7, result.GetProperty("exitStatus").GetProperty("exitCode").GetInt32());
            }

            transport.Receive(Request("wait", "terminal/wait_for_exit", """{"sessionId":"session-1","terminalId":"terminal-1"}"""));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.Equal(7, response.RootElement.GetProperty("result").GetProperty("exitCode").GetInt32());
            }

            transport.Receive(Request("kill", "terminal/kill", """{"sessionId":"session-1","terminalId":"terminal-1"}"""));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.False(response.RootElement.GetProperty("result").EnumerateObject().Any());
            }

            transport.Receive(Request("release", "terminal/release", """{"sessionId":"session-1","terminalId":"terminal-1"}"""));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.False(response.RootElement.GetProperty("result").EnumerateObject().Any());
            }

            Assert.Equal(1, handler.CreateCount);
            Assert.Equal("dotnet", handler.LastCreate!.Command);
            Assert.Equal("--version", Assert.Single(handler.LastCreate.Args!));
            Assert.Equal(cwd, handler.LastCreate.Cwd);
            Assert.Equal("session-1", handler.LastCreate.SessionId);
            Assert.Equal(4096UL, handler.LastCreate.OutputByteLimit);

            Assert.Equal(1, handler.OutputCount);
            Assert.Equal("terminal-1", handler.LastOutput!.TerminalId);
            Assert.Equal("session-1", handler.LastOutput.SessionId);

            Assert.Equal(1, handler.WaitCount);
            Assert.Equal("terminal-1", handler.LastWait!.TerminalId);

            Assert.Equal(1, handler.KillCount);
            Assert.Equal("terminal-1", handler.LastKill!.TerminalId);

            Assert.Equal(1, handler.ReleaseCount);
            Assert.Equal("terminal-1", handler.LastRelease!.TerminalId);
        }
    }

    [Fact]
    public async Task TerminalCreateRejectsInvalidRequestsBeforeCallingTheHandler()
    {
        await using var transport = new TestTransport();
        var handler = new RecordingTerminalHandler();
        var (client, _) = await ConnectAsync(transport, new ClientOptions { TerminalHandler = handler });
        await using (client)
        {
            transport.Receive(Request("empty-command", "terminal/create", """{"sessionId":"session-1","command":"   "}"""));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            }

            transport.Receive(Request("relative-cwd", "terminal/create", """{"sessionId":"session-1","command":"echo","cwd":"relative/path"}"""));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            }

            Assert.Equal(0, handler.CreateCount);
        }
    }

    private static async Task<(AcpClient Client, JsonElement Capabilities)> ConnectAsync(TestTransport transport, ClientOptions options)
    {
        var connecting = AcpClient.ConnectAsync(transport, options, Token);
        using var initialize = await transport.ReadSentAsync();
        transport.Respond(initialize, """{"protocolVersion":1,"agentCapabilities":{}}""");
        var client = await connecting.WaitAsync(TimeSpan.FromSeconds(5), Token);
        return (client, initialize.RootElement.GetProperty("params").GetProperty("clientCapabilities").Clone());
    }

    /// <summary>Creates a live session so that terminal requests naming it are accepted.</summary>
    private static async Task CreateSessionAsync(TestTransport transport, AcpClient client, string sessionId)
    {
        var creating = client.CreateSessionAsync(
            new SessionOptions { WorkingDirectory = Path.GetTempPath() },
            Token);
        using var request = await transport.ReadSentAsync();
        transport.Respond(request, $$"""{"sessionId":"{{sessionId}}"}""");
        _ = await creating.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    private static string Request(string id, string method, string parameters) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + QuoteJsonString(id) + ",\"method\":" + QuoteJsonString(method) + ",\"params\":" + parameters + "}";

    private static string QuoteJsonString(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";

    private sealed class RecordingTerminalHandler : ITerminalHandler
    {
        private int _createCount;
        private int _outputCount;
        private int _waitCount;
        private int _killCount;
        private int _releaseCount;

        public int CreateCount => Volatile.Read(ref _createCount);
        public int OutputCount => Volatile.Read(ref _outputCount);
        public int WaitCount => Volatile.Read(ref _waitCount);
        public int KillCount => Volatile.Read(ref _killCount);
        public int ReleaseCount => Volatile.Read(ref _releaseCount);

        public CreateTerminalRequest? LastCreate { get; private set; }
        public TerminalOutputRequest? LastOutput { get; private set; }
        public WaitForTerminalExitRequest? LastWait { get; private set; }
        public KillTerminalRequest? LastKill { get; private set; }
        public ReleaseTerminalRequest? LastRelease { get; private set; }

        public ValueTask<string> CreateAsync(CreateTerminalRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _createCount);
            LastCreate = request;
            return ValueTask.FromResult("terminal-1");
        }

        public ValueTask<TerminalOutputResponse> GetOutputAsync(TerminalOutputRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _outputCount);
            LastOutput = request;
            return ValueTask.FromResult(new TerminalOutputResponse
            {
                Output = "partial output",
                Truncated = true,
                ExitStatus = new TerminalExitStatus { ExitCode = 7 }
            });
        }

        public ValueTask<TerminalExitStatus> WaitForExitAsync(WaitForTerminalExitRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _waitCount);
            LastWait = request;
            return ValueTask.FromResult(new TerminalExitStatus { ExitCode = 7 });
        }

        public ValueTask KillAsync(KillTerminalRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _killCount);
            LastKill = request;
            return ValueTask.CompletedTask;
        }

        public ValueTask ReleaseAsync(ReleaseTerminalRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _releaseCount);
            LastRelease = request;
            return ValueTask.CompletedTask;
        }
    }
}
