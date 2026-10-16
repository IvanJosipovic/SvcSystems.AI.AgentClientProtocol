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
    public async Task UnexpectedHandlerExceptionsAreRedactedAndReportedToHost()
    {
        await using var transport = new FakeTransport();
        var reported = new TaskCompletionSource<(string Method, Exception Exception)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new JsonRpcConnection(
            transport,
            requestHandlerException: (method, exception) => reported.TrySetResult((method, exception)));
        connection.RegisterRequestHandler("host/fail", (_, _) => throw new InvalidOperationException("C:\\private\\secret.txt"));

        transport.Receive(Json("""{"jsonrpc":"2.0","id":"failure","method":"host/fail","params":{}}"""));

        using var response = await ReadMessageAsync(transport.Sent);
        var error = response.RootElement.GetProperty("error");
        Assert.Equal(-32603, error.GetProperty("code").GetInt32());
        Assert.Equal("Internal error while handling the request.", error.GetProperty("message").GetString());
        Assert.DoesNotContain("secret.txt", response.RootElement.GetRawText(), StringComparison.Ordinal);

        var diagnostic = await reported.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal("host/fail", diagnostic.Method);
        Assert.Equal("C:\\private\\secret.txt", diagnostic.Exception.Message);
    }

    [Fact]
    public async Task IncomingRequestsAreBoundedAndOverCapacityRequestsAreRejected()
    {
        await using var transport = new FakeTransport();
        await using var connection = new JsonRpcConnection(transport, maximumConcurrentIncomingRequests: 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.RegisterRequestHandler("host/slow", async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Encoding.UTF8.GetBytes("{}");
        });

        transport.Receive(Json("""{"jsonrpc":"2.0","id":"first","method":"host/slow","params":{}}"""));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        transport.Receive(Json("""{"jsonrpc":"2.0","id":"second","method":"host/slow","params":{}}"""));

        using var rejected = await ReadMessageAsync(transport.Sent);
        Assert.Equal("second", rejected.RootElement.GetProperty("id").GetString());
        Assert.Equal(-32000, rejected.RootElement.GetProperty("error").GetProperty("code").GetInt32());

        release.TrySetResult();
        using var completed = await ReadMessageAsync(transport.Sent);
        Assert.Equal("first", completed.RootElement.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Object, completed.RootElement.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task DisposalCancelsAndDrainsInFlightIncomingRequests()
    {
        await using var transport = new FakeTransport();
        var connection = new JsonRpcConnection(transport);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.RegisterRequestHandler("host/wait", async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
            return Encoding.UTF8.GetBytes("{}");
        });
        transport.Receive(Json("""{"jsonrpc":"2.0","id":"waiting","method":"host/wait","params":{}}"""));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.True(cancelled.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RpcErrorResponseBecomesRpcException()
    {
        await using var transport = new FakeTransport();
        await using var connection = new JsonRpcConnection(transport);
        var requestTask = connection.SendRequestAsync("bad/method", Json("{}"), CancellationToken.None);
        using var request = await ReadMessageAsync(transport.Sent);
        var requestId = request.RootElement.GetProperty("id").GetInt64();

        transport.Receive(Json($"{{\"jsonrpc\":\"2.0\",\"id\":{requestId},\"error\":{{\"code\":-32601,\"message\":\"missing\"}}}}"));

        var exception = await Assert.ThrowsAsync<RpcException>(() => requestTask);
        Assert.Equal(-32601, exception.Code);
        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("_auth/status_update")]
    [InlineData("$/cancel_request")]
    [InlineData("future/notification")]
    public async Task UnknownNotificationDoesNotInterruptAuthentication(string method)
    {
        await using var transport = new FakeTransport();
        await using var connection = new JsonRpcConnection(transport);
        var authentication = connection.SendRequestAsync("authenticate", Json("{\"methodId\":\"login\"}"), TestContext.Current.CancellationToken);
        using var request = await ReadMessageAsync(transport.Sent);
        var requestId = request.RootElement.GetProperty("id").GetInt64();

        transport.Receive(Json($"{{\"jsonrpc\":\"2.0\",\"method\":\"{method}\",\"params\":{{\"status\":\"pending\"}}}}"));
        transport.Receive(Json($"{{\"jsonrpc\":\"2.0\",\"id\":{requestId},\"result\":{{}}}}"));

        var result = await authentication.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.False(connection.Completion.IsCompleted);
        Assert.False(transport.Sent.TryRead(out _));
    }

    [Fact]
    public async Task RegisteredNotificationsStillDispatch()
    {
        await using var transport = new FakeTransport();
        await using var connection = new JsonRpcConnection(transport);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.RegisterNotificationHandler("_auth/status_update", parameters =>
        {
            received.TrySetResult(parameters.GetProperty("status").GetString()!);
            return ValueTask.CompletedTask;
        });

        transport.Receive(Json("""{"jsonrpc":"2.0","method":"_auth/status_update","params":{"status":"pending"}}"""));

        Assert.Equal("pending", await received.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.False(connection.Completion.IsCompleted);
    }

    [Fact]
    public async Task UnknownRequestStillReturnsMethodNotFound()
    {
        await using var transport = new FakeTransport();
        await using var connection = new JsonRpcConnection(transport);

        transport.Receive(Json("""{"jsonrpc":"2.0","id":"auth-1","method":"_auth/status_update","params":{}}"""));

        using var response = await ReadMessageAsync(transport.Sent);
        Assert.Equal("auth-1", response.RootElement.GetProperty("id").GetString());
        Assert.Equal(-32601, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.False(connection.Completion.IsCompleted);
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

    private sealed class FakeTransport : ITransport
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
