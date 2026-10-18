using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SvcSystems.AI.AgentClientProtocol.Transport;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Agent.Tests;

public sealed class AgentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HandlesInitializeSessionPromptUpdatesAndClientRequests()
    {
        await using var transport = new TestTransport();
        var handler = new CallbackHandler();
        await using var agent = new AcpAgent(handler, transport);
        var run = agent.RunAsync(Token);

        transport.Receive("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""");
        using (var initialize = await transport.ReadSentAsync())
        {
            Assert.Equal(1, initialize.RootElement.GetProperty("result").GetProperty("protocolVersion").GetInt32());
        }

        transport.Receive("""{"jsonrpc":"2.0","id":2,"method":"session/new","params":{"cwd":"/workspace","mcpServers":[]}}""");
        using (var newSession = await transport.ReadSentAsync())
        {
            Assert.Equal("session", newSession.RootElement.GetProperty("result").GetProperty("sessionId").GetString());
        }

        transport.Receive("""{"jsonrpc":"2.0","id":3,"method":"session/prompt","params":{"sessionId":"session","prompt":[{"type":"text","text":"hello"}]}}""");
        using (var update = await transport.ReadSentAsync())
        {
            Assert.Equal("session/update", update.RootElement.GetProperty("method").GetString());
            Assert.Equal("usage_update", update.RootElement.GetProperty("params").GetProperty("update").GetProperty("sessionUpdate").GetString());
        }

        using (var request = await transport.ReadSentAsync())
        {
            Assert.Equal("session/request_permission", request.RootElement.GetProperty("method").GetString());
            transport.Respond(request, """{"outcome":{"outcome":"selected","optionId":"allow"}}""");
        }

        using (var prompt = await transport.ReadSentAsync())
        {
            Assert.Equal(3, prompt.RootElement.GetProperty("id").GetInt32());
            Assert.Equal("end_turn", prompt.RootElement.GetProperty("result").GetProperty("stopReason").GetString());
        }

        Assert.Equal("hello", handler.PromptText);
        Assert.Equal("allow", Assert.IsType<SelectedPermissionOutcome>(handler.PermissionResponse!.Outcome).OptionId);
        transport.CompleteInput();
        await run.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task SessionCancelIsProcessedWhilePromptHandlerIsWaiting()
    {
        await using var transport = new TestTransport();
        var handler = new CancellationHandler();
        await using var agent = new AcpAgent(handler, transport);
        var run = agent.RunAsync(Token);

        transport.Receive("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""");
        using (var initialize = await transport.ReadSentAsync())
        {
            Assert.Equal(1, initialize.RootElement.GetProperty("result").GetProperty("protocolVersion").GetInt32());
        }

        transport.Receive("""{"jsonrpc":"2.0","id":2,"method":"session/new","params":{"cwd":"/workspace","mcpServers":[]}}""");
        using (var newSession = await transport.ReadSentAsync())
        {
            Assert.Equal("session", newSession.RootElement.GetProperty("result").GetProperty("sessionId").GetString());
        }

        transport.Receive("""{"jsonrpc":"2.0","id":3,"method":"session/prompt","params":{"sessionId":"session","prompt":[]}}""");
        await handler.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        transport.Receive("""{"jsonrpc":"2.0","method":"session/cancel","params":{"sessionId":"session"}}""");

        using var prompt = await transport.ReadSentAsync();
        Assert.Equal(3, prompt.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("cancelled", prompt.RootElement.GetProperty("result").GetProperty("stopReason").GetString());
        Assert.True(handler.PromptCancellationObserved);

        transport.CompleteInput();
        await run.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task DispatchesOptionalSessionAndAuthenticationRequests()
    {
        await using var transport = new TestTransport();
        var handler = new OptionalOperationsHandler();
        await using var agent = new AcpAgent(handler, transport);
        var run = agent.RunAsync(Token);

        transport.Receive("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""");
        using (var initialize = await transport.ReadSentAsync())
        {
            Assert.Equal(1, initialize.RootElement.GetProperty("result").GetProperty("protocolVersion").GetInt32());
        }

        var requests = new[]
        {
            ("session/load", """{"sessionId":"s","cwd":"/workspace","mcpServers":[]}"""),
            ("session/resume", """{"sessionId":"s","cwd":"/workspace"}"""),
            ("session/list", "{}"),
            ("session/delete", """{"sessionId":"s"}"""),
            ("session/close", """{"sessionId":"s"}"""),
            ("session/set_mode", """{"sessionId":"s","modeId":"code"}"""),
            ("session/set_config_option", """{"sessionId":"s","configId":"confirm","type":"boolean","value":true}"""),
            ("authenticate", """{"methodId":"token"}"""),
            ("logout", "{}"),
        };

        var requestId = 2;
        foreach (var (method, parameters) in requests)
        {
            transport.Receive($$"""{"jsonrpc":"2.0","id":{{requestId++}},"method":"{{method}}","params":{{parameters}}}""");
            using var response = await transport.ReadSentAsync();
            Assert.True(response.RootElement.TryGetProperty("result", out _), $"{method} did not return a result: {response.RootElement}");
        }

        Assert.Equal(requests.Select(request => request.Item1), handler.Calls);
        transport.CompleteInput();
        await run.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task UnimplementedOptionalAgentMethodReturnsMethodNotFound()
    {
        await using var transport = new TestTransport();
        await using var agent = new AcpAgent(new CallbackHandler(), transport);
        var run = agent.RunAsync(Token);

        transport.Receive("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""");
        using (var initialize = await transport.ReadSentAsync())
        {
            Assert.Equal(1, initialize.RootElement.GetProperty("result").GetProperty("protocolVersion").GetInt32());
        }

        transport.Receive("""{"jsonrpc":"2.0","id":2,"method":"session/list","params":{}}""");
        using var response = await transport.ReadSentAsync();
        Assert.Equal(-32601, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());

        transport.CompleteInput();
        await run.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task TypedClientRequestHelpersExchangeGeneratedModels()
    {
        await using var transport = new TestTransport();
        var handler = new ClientRequestHandler();
        await using var agent = new AcpAgent(handler, transport);
        var run = agent.RunAsync(Token);

        transport.Receive("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""");
        using (var initialize = await transport.ReadSentAsync())
        {
            Assert.Equal(1, initialize.RootElement.GetProperty("result").GetProperty("protocolVersion").GetInt32());
        }

        transport.Receive("""{"jsonrpc":"2.0","id":2,"method":"session/new","params":{"cwd":"/workspace","mcpServers":[]}}""");
        using (var newSession = await transport.ReadSentAsync())
        {
            Assert.Equal("session", newSession.RootElement.GetProperty("result").GetProperty("sessionId").GetString());
        }

        transport.Receive("""{"jsonrpc":"2.0","id":3,"method":"session/prompt","params":{"sessionId":"session","prompt":[]}}""");

        using (var request = await transport.ReadSentAsync())
        {
            Assert.Equal("fs/read_text_file", request.RootElement.GetProperty("method").GetString());
            Assert.Equal("notes.txt", request.RootElement.GetProperty("params").GetProperty("path").GetString());
            transport.Respond(request, """{"content":"hello"}""");
        }

        using (var request = await transport.ReadSentAsync())
        {
            Assert.Equal("fs/write_text_file", request.RootElement.GetProperty("method").GetString());
            Assert.Equal("done", request.RootElement.GetProperty("params").GetProperty("content").GetString());
            transport.Respond(request, "{}");
        }

        using (var request = await transport.ReadSentAsync())
        {
            Assert.Equal("terminal/create", request.RootElement.GetProperty("method").GetString());
            Assert.Equal("echo", request.RootElement.GetProperty("params").GetProperty("command").GetString());
            transport.Respond(request, """{"terminalId":"terminal-1"}""");
        }

        using (var request = await transport.ReadSentAsync())
        {
            Assert.Equal("terminal/output", request.RootElement.GetProperty("method").GetString());
            Assert.Equal("terminal-1", request.RootElement.GetProperty("params").GetProperty("terminalId").GetString());
            transport.Respond(request, """{"output":"hi","truncated":false}""");
        }

        using (var request = await transport.ReadSentAsync())
        {
            Assert.Equal("terminal/wait_for_exit", request.RootElement.GetProperty("method").GetString());
            transport.Respond(request, """{"exitCode":0}""");
        }

        using (var request = await transport.ReadSentAsync())
        {
            Assert.Equal("terminal/kill", request.RootElement.GetProperty("method").GetString());
            transport.Respond(request, "{}");
        }

        using (var request = await transport.ReadSentAsync())
        {
            Assert.Equal("terminal/release", request.RootElement.GetProperty("method").GetString());
            transport.Respond(request, "{}");
        }

        using (var request = await transport.ReadSentAsync())
        {
            Assert.Equal("session/request_permission", request.RootElement.GetProperty("method").GetString());
            transport.Respond(request, """{"outcome":{"outcome":"selected","optionId":"allow"}}""");
        }

        using (var request = await transport.ReadSentAsync())
        {
            Assert.Equal("elicitation/create", request.RootElement.GetProperty("method").GetString());
            Assert.Equal("form", request.RootElement.GetProperty("params").GetProperty("mode").GetString());
            Assert.Equal("Sign in", request.RootElement.GetProperty("params").GetProperty("message").GetString());
            transport.Respond(request, """{"action":"accept","content":{"answer":"yes"}}""");
        }

        using (var notification = await transport.ReadSentAsync())
        {
            Assert.Equal("elicitation/complete", notification.RootElement.GetProperty("method").GetString());
            Assert.Equal("elicitation-1", notification.RootElement.GetProperty("params").GetProperty("elicitationId").GetString());
        }

        using (var prompt = await transport.ReadSentAsync())
        {
            Assert.Equal(3, prompt.RootElement.GetProperty("id").GetInt32());
            Assert.Equal("end_turn", prompt.RootElement.GetProperty("result").GetProperty("stopReason").GetString());
        }

        Assert.Equal("hello", handler.FileContent);
        Assert.Equal("terminal-1", handler.TerminalId);
        Assert.True(handler.TerminalWasTruncated is false);
        Assert.Equal("hi", handler.TerminalOutput);
        Assert.Equal(0u, handler.ExitCode);
        Assert.Equal("allow", handler.PermissionOptionId);
        Assert.Equal("yes", handler.ElicitationAnswer);
        Assert.True(handler.ElicitationCompleted);

        transport.CompleteInput();
        await run.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    private sealed class ClientRequestHandler : IAgentHandler
    {
        public string? FileContent { get; private set; }

        public string? TerminalId { get; private set; }

        public string? TerminalOutput { get; private set; }

        public bool? TerminalWasTruncated { get; private set; }

        public uint? ExitCode { get; private set; }

        public string? PermissionOptionId { get; private set; }

        public string? ElicitationAnswer { get; private set; }

        public bool ElicitationCompleted { get; private set; }

        public ValueTask<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new InitializeResponse { ProtocolVersion = request.ProtocolVersion });

        public ValueTask<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NewSessionResponse { SessionId = "session" });

        public async ValueTask<PromptResponse> PromptAsync(
            PromptRequest request,
            IAgentContext context,
            CancellationToken cancellationToken)
        {
            FileContent = (await context.ReadTextFileAsync(
                new ReadTextFileRequest { SessionId = request.SessionId, Path = "notes.txt" }, cancellationToken)).Content;
            await context.WriteTextFileAsync(
                new WriteTextFileRequest { SessionId = request.SessionId, Path = "out.txt", Content = "done" }, cancellationToken);
            TerminalId = (await context.CreateTerminalAsync(
                new CreateTerminalRequest { SessionId = request.SessionId, Command = "echo", Args = ["hi"] }, cancellationToken)).TerminalId;
            var output = await context.GetTerminalOutputAsync(
                new TerminalOutputRequest { SessionId = request.SessionId, TerminalId = TerminalId }, cancellationToken);
            TerminalOutput = output.Output;
            TerminalWasTruncated = output.Truncated;
            ExitCode = (await context.WaitForTerminalExitAsync(
                new WaitForTerminalExitRequest { SessionId = request.SessionId, TerminalId = TerminalId }, cancellationToken)).ExitCode;
            await context.KillTerminalAsync(
                new KillTerminalRequest { SessionId = request.SessionId, TerminalId = TerminalId }, cancellationToken);
            await context.ReleaseTerminalAsync(
                new ReleaseTerminalRequest { SessionId = request.SessionId, TerminalId = TerminalId }, cancellationToken);
            var permission = await context.RequestPermissionAsync(
                new RequestPermissionRequest
                {
                    SessionId = request.SessionId,
                    ToolCall = new ToolCallUpdate { ToolCallId = "tool", Title = "Read" },
                    Options = [new PermissionOption { OptionId = "allow", Name = "Allow", Kind = "allow_once" }]
                },
                cancellationToken);
            PermissionOptionId = Assert.IsType<SelectedPermissionOutcome>(permission.Outcome).OptionId;
            var elicitation = await context.CreateElicitationAsync(
                new ElicitationFormMode
                {
                    Message = "Sign in",
                    RequestedSchema = new ElicitationSchema { Type = "object" },
                    SessionId = request.SessionId
                },
                cancellationToken);
            ElicitationAnswer = Assert.IsType<string>(
                Assert.IsType<ElicitationAcceptAction>(elicitation.Value).Content!["answer"].Value);
            await context.CompleteElicitationAsync(
                new CompleteElicitationNotification { ElicitationId = "elicitation-1" }, cancellationToken);
            ElicitationCompleted = true;
            return new PromptResponse { StopReason = "end_turn" };
        }
    }

    private sealed class CallbackHandler : IAgentHandler
    {
        public string? PromptText { get; private set; }

        public RequestPermissionResponse? PermissionResponse { get; private set; }

        public ValueTask<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new Implementation { Name = "test-agent", Version = "1.0.0" },
            });

        public ValueTask<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NewSessionResponse { SessionId = "session" });

        public async ValueTask<PromptResponse> PromptAsync(
            PromptRequest request,
            IAgentContext context,
            CancellationToken cancellationToken)
        {
            PromptText = Assert.IsType<TextContent>(Assert.Single(request.Prompt).Value).Text;
            await context.SendSessionUpdateAsync(request.SessionId, new UsageUpdate { Used = 1, Size = 10 }, cancellationToken);
            PermissionResponse = await context.RequestPermissionAsync(
                new RequestPermissionRequest
                {
                    SessionId = request.SessionId,
                    ToolCall = new ToolCallUpdate { ToolCallId = "tool", Title = "Read" },
                    Options = [new PermissionOption { OptionId = "allow", Name = "Allow", Kind = "allow_once" }]
                },
                cancellationToken);
            return new PromptResponse { StopReason = "end_turn" };
        }
    }

    private sealed class CancellationHandler : IAgentHandler
    {
        public TaskCompletionSource PromptStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool PromptCancellationObserved { get; private set; }

        public ValueTask<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new InitializeResponse { ProtocolVersion = request.ProtocolVersion });

        public ValueTask<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NewSessionResponse { SessionId = "session" });

        public async ValueTask<PromptResponse> PromptAsync(
            PromptRequest request,
            IAgentContext context,
            CancellationToken cancellationToken)
        {
            PromptStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                PromptCancellationObserved = true;
                return new PromptResponse { StopReason = "cancelled" };
            }

            throw new InvalidOperationException("The test prompt should only finish through cancellation.");
        }
    }

    private sealed class OptionalOperationsHandler : IAgentHandler
    {
        public List<string> Calls { get; } = [];

        public ValueTask<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new InitializeResponse { ProtocolVersion = request.ProtocolVersion });

        public ValueTask<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NewSessionResponse { SessionId = "session" });

        public ValueTask<PromptResponse> PromptAsync(PromptRequest request, IAgentContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PromptResponse { StopReason = "end_turn" });

        public ValueTask<LoadSessionResponse> LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("session/load");
            return ValueTask.FromResult(new LoadSessionResponse());
        }

        public ValueTask<ResumeSessionResponse> ResumeSessionAsync(ResumeSessionRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("session/resume");
            return ValueTask.FromResult(new ResumeSessionResponse());
        }

        public ValueTask<ListSessionsResponse> ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("session/list");
            return ValueTask.FromResult(new ListSessionsResponse { Sessions = [] });
        }

        public ValueTask<DeleteSessionResponse> DeleteSessionAsync(DeleteSessionRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("session/delete");
            return ValueTask.FromResult(new DeleteSessionResponse());
        }

        public ValueTask<CloseSessionResponse> CloseSessionAsync(CloseSessionRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("session/close");
            return ValueTask.FromResult(new CloseSessionResponse());
        }

        public ValueTask<SetSessionModeResponse> SetSessionModeAsync(SetSessionModeRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("session/set_mode");
            return ValueTask.FromResult(new SetSessionModeResponse());
        }

        public ValueTask<SetSessionConfigOptionResponse> SetSessionConfigOptionAsync(
            SetSessionConfigOptionRequest request,
            CancellationToken cancellationToken)
        {
            Calls.Add("session/set_config_option");
            return ValueTask.FromResult(new SetSessionConfigOptionResponse { ConfigOptions = [] });
        }

        public ValueTask<AuthenticateResponse> AuthenticateAsync(AuthenticateRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("authenticate");
            return ValueTask.FromResult(new AuthenticateResponse());
        }

        public ValueTask<LogoutResponse> LogoutAsync(LogoutRequest request, CancellationToken cancellationToken)
        {
            Calls.Add("logout");
            return ValueTask.FromResult(new LogoutResponse());
        }
    }

    private sealed class TestTransport : ITransport
    {
        private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly Channel<ReadOnlyMemory<byte>> _sent = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public void Receive(string message) => _incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(message));

        public void Respond(JsonDocument request, string result) =>
            Receive($$"""{"jsonrpc":"2.0","id":{{request.RootElement.GetProperty("id").GetRawText()}},"result":{{result}}}""");

        public void CompleteInput() => _incoming.Writer.TryComplete();

        public async Task<JsonDocument> ReadSentAsync() =>
            JsonDocument.Parse(await _sent.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));

        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) =>
            _sent.Writer.WriteAsync(message.ToArray(), cancellationToken);

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var message in _incoming.Reader.ReadAllAsync(cancellationToken))
            {
                yield return message;
            }
        }

        public ValueTask DisposeAsync()
        {
            _incoming.Writer.TryComplete();
            _sent.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
