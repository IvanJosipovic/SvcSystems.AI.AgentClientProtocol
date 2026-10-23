using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SvcSystems.AI.AgentClientProtocol.Agent;
using SvcSystems.AI.AgentClientProtocol.Agent.Models;
using SvcSystems.AI.AgentClientProtocol.Transport;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Agent.Tests;

/// <summary>Stress coverage for the agent host's deliberate-stop path.</summary>
public sealed class AgentShutdownStressTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StoppingTheHostWithAnActivePromptAlwaysEndsTheRunCleanly()
    {
        // The stop races the receive loop's teardown. Every iteration must end cleanly.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var promptEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var transport = new StressTransport();
            await using var agent = new Agent(new StressHandler(promptEntered), transport);
            var run = agent.RunAsync(Token);

            await InitializeAsync(transport);
            transport.Receive("""{"jsonrpc":"2.0","id":3,"method":"session/prompt","params":{"sessionId":"session","prompt":[]}}""");
            await promptEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

            await agent.StopAsync();

            // The run must complete rather than hang or fault.
            var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10), Token));
            Assert.Same(run, completed);
            await run;

            // A deliberate stop is a clean shutdown.
            Assert.Null(await agent.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token));
        }
    }

    private static async Task InitializeAsync(StressTransport transport)
    {
        transport.Receive("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""");
        using var initialize = await transport.ReadSentAsync();
        transport.Receive("""{"jsonrpc":"2.0","id":2,"method":"session/new","params":{"cwd":"/workspace","mcpServers":[]}}""");
        using var session = await transport.ReadSentAsync();
    }

    private sealed class StressHandler(TaskCompletionSource promptEntered) : IAgentHandler
    {
        public ValueTask<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new InitializeResponse { ProtocolVersion = 1 });

        public ValueTask<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NewSessionResponse { SessionId = "session" });

        public async ValueTask<PromptResponse> PromptAsync(
            PromptRequest request,
            IAgentContext context,
            CancellationToken cancellationToken)
        {
            promptEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new PromptResponse { StopReason = "end_turn" };
        }
    }

    private sealed class StressTransport : ITransport
    {
        private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly Channel<ReadOnlyMemory<byte>> _sent = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public void Receive(string message) => _incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(message));

        public async Task<JsonDocument> ReadSentAsync() =>
            JsonDocument.Parse(await _sent.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token));

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
