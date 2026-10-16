using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SvcSystems.AI.AgentClientProtocol;
using SvcSystems.AI.AgentClientProtocol.JsonRpc;
using SvcSystems.AI.AgentClientProtocol.Transport;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

public sealed class JsonRpcConnectionTests
{
    [Fact]
    public async Task ConcurrentRequestsAreCorrelatedById()
    {
        await using var transport = new FakeTransport();
        await using var connection = new JsonRpcConnection(transport);
        var firstTask = connection.SendRequestAsync("first", Json("{}"), CancellationToken.None);
        var secondTask = connection.SendRequestAsync("second", Json("{}"), CancellationToken.None);

        var firstRequest = await ReadMessageAsync(transport.Sent);
        var secondRequest = await ReadMessageAsync(transport.Sent);
        var firstId = firstRequest.RootElement.GetProperty("id").GetInt64();
        var secondId = secondRequest.RootElement.GetProperty("id").GetInt64();

        transport.Receive(Json($"{{\"jsonrpc\":\"2.0\",\"id\":{secondId},\"result\":{{\"sequence\":2}}}}"));
        transport.Receive(Json($"{{\"jsonrpc\":\"2.0\",\"id\":{firstId},\"result\":{{\"sequence\":1}}}}"));

        var first = await firstTask;
        var second = await secondTask;
        Assert.Equal(1, first.GetProperty("sequence").GetInt32());
        Assert.Equal(2, second.GetProperty("sequence").GetInt32());
    }

    [Fact]
    public async Task IncomingRequestPreservesStringIdAndReturnsTypedResultEnvelope()
    {
        await using var transport = new FakeTransport();
        await using var connection = new JsonRpcConnection(transport);
        connection.RegisterRequestHandler("host/ping", (_, _) => ValueTask.FromResult(Encoding.UTF8.GetBytes("{\"pong\":true}")));

        transport.Receive(Json("{\"jsonrpc\":\"2.0\",\"id\":\"request-9\",\"method\":\"host/ping\",\"params\":{}}"));

        using var response = await ReadMessageAsync(transport.Sent);
        Assert.Equal("request-9", response.RootElement.GetProperty("id").GetString());
        Assert.True(response.RootElement.GetProperty("result").GetProperty("pong").GetBoolean());
    }

    [Fact]
    public async Task RpcErrorResponseBecomesAcpRpcException()
    {
        await using var transport = new FakeTransport();
        await using var connection = new JsonRpcConnection(transport);
        var requestTask = connection.SendRequestAsync("bad/method", Json("{}"), CancellationToken.None);
        using var request = await ReadMessageAsync(transport.Sent);
        var requestId = request.RootElement.GetProperty("id").GetInt64();

        transport.Receive(Json($"{{\"jsonrpc\":\"2.0\",\"id\":{requestId},\"error\":{{\"code\":-32601,\"message\":\"missing\"}}}}"));

        var exception = await Assert.ThrowsAsync<AcpRpcException>(() => requestTask);
        Assert.Equal(-32601, exception.Code);
        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReflectionSerializationIsDisabledForTheTestConsumer()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
    }

    private static byte[] Json(string value) => Encoding.UTF8.GetBytes(value);

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<JsonDocument> ReadMessageAsync(ChannelReader<ReadOnlyMemory<byte>> reader)
    {
        var message = await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        return JsonDocument.Parse(message);
    }

    private sealed class FakeTransport : IAcpTransport
    {
        private readonly Channel<ReadOnlyMemory<byte>> _received = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly Channel<ReadOnlyMemory<byte>> _sent = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public ChannelReader<ReadOnlyMemory<byte>> Sent => _sent.Reader;

        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) =>
            _sent.Writer.WriteAsync(message.ToArray(), cancellationToken);

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in _received.Reader.ReadAllAsync(cancellationToken)) yield return item;
        }

        public void Receive(byte[] message) => _received.Writer.TryWrite(message);

        public ValueTask DisposeAsync()
        {
            _received.Writer.TryComplete();
            _sent.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
