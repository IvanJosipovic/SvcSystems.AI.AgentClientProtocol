using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SvcSystems.AI.AgentClientProtocol.JsonRpc;
using SvcSystems.AI.AgentClientProtocol.Transport;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Agent.Tests;

public sealed class AgentHostBehaviourTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task InvalidResourceLimitsAreRejected(int maximumConcurrentIncomingRequests, int maximumMessageBytes)
    {
        await using var transport = new ChannelTransport();

        Assert.Throws<ArgumentOutOfRangeException>(() => new AcpAgent(
            new MinimalHandler(),
            transport,
            new AgentOptions
            {
                MaximumConcurrentIncomingRequests = maximumConcurrentIncomingRequests,
                MaximumMessageBytes = maximumMessageBytes
            }));
    }

    [Fact]
    public async Task StoppingAnIdleHostCompletesTheConnection()
    {
        await using var transport = new ChannelTransport();
        await using var agent = new AcpAgent(new MinimalHandler(), transport);

        Assert.False(agent.Completion.IsCompleted);

        await agent.StopAsync();

        Assert.Null(await agent.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token));
    }

    [Fact]
    public async Task RunningTheHostTwiceIsRejected()
    {
        await using var transport = new ChannelTransport();
        await using var agent = new AcpAgent(new MinimalHandler(), transport);
        var run = agent.RunAsync(Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync(Token));

        transport.CompleteInput();
        await run.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task MalformedInputFaultsTheRunLoop()
    {
        await using var transport = new ChannelTransport();
        await using var agent = new AcpAgent(new MinimalHandler(), transport);
        var run = agent.RunAsync(Token);

        transport.Receive("{not json");

        await Assert.ThrowsAsync<ProtocolException>(() => run.WaitAsync(TimeSpan.FromSeconds(5), Token));
    }

    [Fact]
    public async Task AnUnsupportedProtocolVersionIsAnsweredWithTheSupportedVersion()
    {
        await using var transport = new ChannelTransport();
        await using var agent = new AcpAgent(new MinimalHandler(), transport);
        var run = agent.RunAsync(Token);

        // The client asks for a protocol version this host does not speak. ACP requires a
        // successful result carrying the agent's own version, not an error and not an echo of
        // the unsupported version.
        transport.Receive("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":65535}}""");

        using var response = await transport.ReadSentAsync();
        Assert.True(response.RootElement.TryGetProperty("result", out var result));
        Assert.Equal(1, result.GetProperty("protocolVersion").GetInt32());

        transport.CompleteInput();
        await run.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task TheSupportedProtocolVersionIsAccepted()
    {
        await using var transport = new ChannelTransport();
        await using var agent = new AcpAgent(new MinimalHandler(), transport);
        var run = agent.RunAsync(Token);

        transport.Receive("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""");

        using var response = await transport.ReadSentAsync();
        Assert.True(response.RootElement.TryGetProperty("result", out var result));
        Assert.Equal(1, result.GetProperty("protocolVersion").GetInt32());

        transport.CompleteInput();
        await run.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Theory]
    [InlineData("session/delete", "null")]
    [InlineData("session/resume", "\"text\"")]
    public async Task InvalidOptionalRequestParametersAreRejected(string method, string parameters)
    {
        await using var transport = new ChannelTransport();
        await using var agent = new AcpAgent(new MinimalHandler(), transport);
        var run = agent.RunAsync(Token);

        transport.Receive($$"""{"jsonrpc":"2.0","id":7,"method":"{{method}}","params":{{parameters}}}""");

        using var response = await transport.ReadSentAsync();
        Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());

        transport.CompleteInput();
        await run.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task ConcurrentPromptsForOneSessionAreRejected()
    {
        await using var transport = new ChannelTransport();
        var handler = new BlockingPromptHandler();
        await using var agent = new AcpAgent(handler, transport);
        var run = agent.RunAsync(Token);

        await InitializeSessionAsync(transport);
        transport.Receive("""{"jsonrpc":"2.0","id":3,"method":"session/prompt","params":{"sessionId":"session","prompt":[]}}""");
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        transport.Receive("""{"jsonrpc":"2.0","id":4,"method":"session/prompt","params":{"sessionId":"session","prompt":[]}}""");
        using (var rejected = await transport.ReadSentAsync())
        {
            Assert.Equal(4, rejected.RootElement.GetProperty("id").GetInt32());
            Assert.Equal(-32602, rejected.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }

        handler.Release.TrySetResult();
        using (var completed = await transport.ReadSentAsync())
        {
            Assert.Equal(3, completed.RootElement.GetProperty("id").GetInt32());
            Assert.Equal("end_turn", completed.RootElement.GetProperty("result").GetProperty("stopReason").GetString());
        }

        transport.CompleteInput();
        await run.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task UncaughtPromptCancellationReturnsCancelled()
    {
        await using var transport = new ChannelTransport();
        var handler = new SuspendingPromptHandler(throwOnCancellation: false);
        await using var agent = new AcpAgent(handler, transport);
        var run = agent.RunAsync(Token);

        await InitializeSessionAsync(transport);
        transport.Receive("""{"jsonrpc":"2.0","id":3,"method":"session/prompt","params":{"sessionId":"session","prompt":[]}}""");
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        transport.Receive("""{"jsonrpc":"2.0","method":"session/cancel","params":{"sessionId":"session"}}""");

        using (var completed = await transport.ReadSentAsync())
        {
            Assert.Equal(3, completed.RootElement.GetProperty("id").GetInt32());
            Assert.Equal("cancelled", completed.RootElement.GetProperty("result").GetProperty("stopReason").GetString());
        }
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        transport.CompleteInput();
        await run.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task ThrowingCancellationCallbacksDoNotBreakPromptCancellation()
    {
        await using var transport = new ChannelTransport();
        var handler = new SuspendingPromptHandler(throwOnCancellation: true);
        await using var agent = new AcpAgent(handler, transport);
        var run = agent.RunAsync(Token);

        await InitializeSessionAsync(transport);
        transport.Receive("""{"jsonrpc":"2.0","id":3,"method":"session/prompt","params":{"sessionId":"session","prompt":[]}}""");
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        transport.Receive("""{"jsonrpc":"2.0","method":"session/cancel","params":{"sessionId":"session"}}""");

        using (var completed = await transport.ReadSentAsync())
        {
            Assert.Equal("cancelled", completed.RootElement.GetProperty("result").GetProperty("stopReason").GetString());
        }
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        transport.CompleteInput();
        await run.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task StoppingTheHostCancelsActivePrompts()
    {
        await using var transport = new ChannelTransport();
        var handler = new SuspendingPromptHandler(throwOnCancellation: true);
        await using var agent = new AcpAgent(handler, transport);
        var run = agent.RunAsync(Token);

        await InitializeSessionAsync(transport);
        transport.Receive("""{"jsonrpc":"2.0","id":3,"method":"session/prompt","params":{"sessionId":"session","prompt":[]}}""");
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        await agent.StopAsync();

        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        // Stopping the host deliberately is a clean shutdown, not a failure: the run must not
        // surface a cancellation or a transport error to the caller.
        await run.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Null(await agent.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token));
    }

    [Fact]
    public async Task StoppingTheHostRepeatedlyStillEndsTheRunCleanly()
    {
        // A deliberate stop races the receive loop's own teardown. Repeating the stop exercises
        // that race: the run must end cleanly every time, and never surface a cancellation.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await using var transport = new ChannelTransport();
            var handler = new SuspendingPromptHandler(throwOnCancellation: true);
            await using var agent = new AcpAgent(handler, transport);
            var run = agent.RunAsync(Token);

            await InitializeSessionAsync(transport);
            transport.Receive("""{"jsonrpc":"2.0","id":3,"method":"session/prompt","params":{"sessionId":"session","prompt":[]}}""");
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

            await agent.StopAsync();

            await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await run.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Null(await agent.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token));
        }
    }

    [Fact]
    public async Task InvalidJsonParametersAreRejectedByTheContext()
    {
        await using var transport = new ChannelTransport();
        await using var connection = new JsonRpcConnection(transport);
        var context = new AgentContext(connection);
        using var parameters = JsonDocument.Parse("5");

        await Assert.ThrowsAsync<ArgumentException>(
            () => context.SendClientRequestAsync("_custom/request", parameters.RootElement, Token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => context.SendClientNotificationAsync("_custom/notification", parameters.RootElement, Token).AsTask());
    }

    [Fact]
    public async Task OptionalHandlerMethodsAreUnsupportedByDefault()
    {
        IAgentHandler handler = new MinimalHandler();

        await AssertUnsupportedAsync(handler.LoadSessionAsync(
            new LoadSessionRequest { SessionId = "session", Cwd = "/workspace", McpServers = [] }, Token), "session/load");
        await AssertUnsupportedAsync(handler.ResumeSessionAsync(
            new ResumeSessionRequest { SessionId = "session", Cwd = "/workspace" }, Token), "session/resume");
        await AssertUnsupportedAsync(handler.ListSessionsAsync(new ListSessionsRequest(), Token), "session/list");
        await AssertUnsupportedAsync(handler.DeleteSessionAsync(
            new DeleteSessionRequest { SessionId = "session" }, Token), "session/delete");
        await AssertUnsupportedAsync(handler.CloseSessionAsync(
            new CloseSessionRequest { SessionId = "session" }, Token), "session/close");
        await AssertUnsupportedAsync(handler.SetSessionModeAsync(
            new SetSessionModeRequest { SessionId = "session", ModeId = "code" }, Token), "session/set_mode");
        await AssertUnsupportedAsync(handler.SetSessionConfigOptionAsync(default, Token), "session/set_config_option");
        await AssertUnsupportedAsync(handler.AuthenticateAsync(
            new AuthenticateRequest { MethodId = "token" }, Token), "authenticate");
        await AssertUnsupportedAsync(handler.LogoutAsync(new LogoutRequest(), Token), "logout");
    }

    private static async Task AssertUnsupportedAsync<T>(ValueTask<T> operation, string method)
    {
        var exception = await Assert.ThrowsAsync<RpcException>(() => operation.AsTask());
        Assert.Equal(-32601, exception.Code);
        Assert.Equal(method, exception.Method);
    }

    private static async Task InitializeSessionAsync(ChannelTransport transport)
    {
        transport.Receive("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""");
        using (var initialize = await transport.ReadSentAsync())
        {
            Assert.True(initialize.RootElement.TryGetProperty("result", out _));
        }

        transport.Receive("""{"jsonrpc":"2.0","id":2,"method":"session/new","params":{"cwd":"/workspace","mcpServers":[]}}""");
        using var session = await transport.ReadSentAsync();
        Assert.Equal("session", session.RootElement.GetProperty("result").GetProperty("sessionId").GetString());
    }

    private sealed class MinimalHandler : IAgentHandler
    {
        public ValueTask<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new InitializeResponse { ProtocolVersion = request.ProtocolVersion });

        public ValueTask<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NewSessionResponse { SessionId = "session" });

        public ValueTask<PromptResponse> PromptAsync(
            PromptRequest request,
            IAgentContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PromptResponse { StopReason = "end_turn" });
    }

    private sealed class BlockingPromptHandler : IAgentHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new InitializeResponse { ProtocolVersion = request.ProtocolVersion });

        public ValueTask<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NewSessionResponse { SessionId = "session" });

        public async ValueTask<PromptResponse> PromptAsync(
            PromptRequest request,
            IAgentContext context,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new PromptResponse { StopReason = "end_turn" };
        }
    }

    private sealed class SuspendingPromptHandler(bool throwOnCancellation) : IAgentHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new InitializeResponse { ProtocolVersion = request.ProtocolVersion });

        public ValueTask<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NewSessionResponse { SessionId = "session" });

        public async ValueTask<PromptResponse> PromptAsync(
            PromptRequest request,
            IAgentContext context,
            CancellationToken cancellationToken)
        {
            using var registration = throwOnCancellation
                ? cancellationToken.Register(() => throw new InvalidOperationException("cancellation callback failure"))
                : default;
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }

            return new PromptResponse { StopReason = "end_turn" };
        }
    }

    private sealed class ChannelTransport : ITransport
    {
        private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly Channel<ReadOnlyMemory<byte>> _sent = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public void Receive(string message) => _incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(message));

        public void CompleteInput() => _incoming.Writer.TryComplete();

        public async Task<JsonDocument> ReadSentAsync() =>
            JsonDocument.Parse(await _sent.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));

        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) =>
            _sent.Writer.WriteAsync(message.ToArray(), cancellationToken);

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var message in _incoming.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
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
