using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SvcSystems.AI.AgentClientProtocol.Transport;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

/// <summary>In-memory transport for deterministic client/JSON-RPC unit tests.</summary>
internal sealed class TestTransport : ITransport
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    private readonly Channel<ReadOnlyMemory<byte>> _sent = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

    public void Receive(string json) => Assert.True(_incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(json)));

    public void Respond(JsonDocument request, string result) =>
        Receive($$"""{"jsonrpc":"2.0","id":{{request.RootElement.GetProperty("id").GetRawText()}},"result":{{result}}}""");

    public void RespondError(JsonDocument request, int code, string message) =>
        Receive($$$"""{"jsonrpc":"2.0","id":{{{request.RootElement.GetProperty("id").GetRawText()}}},"error":{"code":{{{code}}},"message":"{{{message}}}"}}""");

    public async Task<JsonDocument> ReadSentAsync() =>
        JsonDocument.Parse(await _sent.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));

    /// <summary>Optional hook that delays individual outbound messages (used to simulate a blocked peer).</summary>
    public Func<ReadOnlyMemory<byte>, Task>? SendGate { get; set; }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        if (SendGate is { } gate)
        {
            await gate(message).ConfigureAwait(false);
        }

        await _sent.Writer.WriteAsync(message.ToArray(), cancellationToken).ConfigureAwait(false);
    }

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
