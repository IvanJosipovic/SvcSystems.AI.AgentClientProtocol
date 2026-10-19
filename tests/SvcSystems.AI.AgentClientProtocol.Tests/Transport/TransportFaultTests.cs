using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SvcSystems.AI.AgentClientProtocol.Transport;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

public sealed class TransportFaultTests
{
    [Fact]
    public async Task TransportFaultIsRaisedWhenTheAgentClosesTheConnection()
    {
        await using var transport = new InitializeTransport();
        await using var client = await ConnectAsync(transport);
        var faulted = new TaskCompletionSource<TransportFaultedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.TransportFaulted += (_, args) => faulted.TrySetResult(args);

        // Simulate the agent closing its stdout while the connection is active.
        transport.CloseOutput();

        var args = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.IsType<ProcessException>(args.Exception);
        Assert.Same(args.Exception, await client.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AThrowingHandlerDoesNotStopOtherHandlersFromBeingNotified()
    {
        await using var transport = new InitializeTransport();
        await using var client = await ConnectAsync(transport);
        var faulted = new TaskCompletionSource<TransportFaultedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.TransportFaulted += (_, _) => throw new InvalidOperationException("This handler always throws.");
        client.TransportFaulted += (_, args) => faulted.TrySetResult(args);

        transport.CloseOutput();

        var args = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.IsType<ProcessException>(args.Exception);
    }

    [Fact]
    public async Task NoFaultIsRaisedForACleanShutdown()
    {
        await using var transport = new InitializeTransport();
        var client = await ConnectAsync(transport);
        var faulted = new TaskCompletionSource<TransportFaultedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.TransportFaulted += (_, args) => faulted.TrySetResult(args);

        await client.DisposeAsync();

        Assert.Null(await client.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        // The event is raised from an asynchronous continuation, so observe a bounded window before
        // asserting it did not fire; an immediate check could pass before a (wrong) raise happens.
        var winner = await Task.WhenAny(faulted.Task, Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken));
        Assert.NotSame(faulted.Task, winner);
    }

    private static async Task<AcpClient> ConnectAsync(InitializeTransport transport)
    {
        var connecting = AcpClient.ConnectAsync(transport, null, TestContext.Current.CancellationToken);
        using var request = await transport.ReadSentAsync();
        transport.Respond(request, "{\"protocolVersion\":1}");
        return await connecting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    private sealed class InitializeTransport : ITransport
    {
        private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly Channel<ReadOnlyMemory<byte>> _sent = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) =>
            await _sent.Writer.WriteAsync(message.ToArray(), cancellationToken).ConfigureAwait(false);

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var message in _incoming.Reader.ReadAllAsync(cancellationToken)) yield return message;
        }

        public async Task<JsonDocument> ReadSentAsync() =>
            JsonDocument.Parse(await _sent.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        public void Respond(JsonDocument request, string result) =>
            Receive($$"""{"jsonrpc":"2.0","id":{{request.RootElement.GetProperty("id").GetRawText()}},"result":{{result}}}""");

        /// <summary>Queues one inbound frame.</summary>
        public void Receive(string json) => _incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(json));

        /// <summary>Completes the inbound stream to simulate the agent closing its stdout.</summary>
        public void CloseOutput() => _incoming.Writer.TryComplete();

        public ValueTask DisposeAsync()
        {
            _incoming.Writer.TryComplete();
            _sent.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
