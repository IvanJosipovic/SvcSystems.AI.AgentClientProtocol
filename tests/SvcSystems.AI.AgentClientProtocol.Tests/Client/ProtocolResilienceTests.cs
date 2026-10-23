using System.Text;
using System.Text.Json;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

/// <summary>
/// Covers the protocol-resilience guarantees: interoperability with strict peers, session
/// registration that cannot race a streaming agent, and tolerance for malformed messages.
/// </summary>
public sealed class ProtocolResilienceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CancelRequestNotificationUsesTheSchemaRequestIdProperty()
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = client.SendExtensionRequestAsync("_vendor/wait", Element("{}"), cancellation.Token);
        using var request = await transport.ReadSentAsync();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        using var notification = await transport.ReadSentAsync();
        Assert.Equal("$/cancel_request", notification.RootElement.GetProperty("method").GetString());
        var parameters = notification.RootElement.GetProperty("params");
        // The ACP schema names the cancelled request "requestId"; a strict peer ignores "id".
        Assert.Equal(request.RootElement.GetProperty("id").GetInt64(), parameters.GetProperty("requestId").GetInt64());
        Assert.False(parameters.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task SessionUpdatesSentImmediatelyAfterTheSessionNewResponseAreNotLost()
    {
        // An agent commonly answers session/new and immediately streams its first update. The
        // session must be registered before that notification is processed.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await using var transport = new TestTransport();
            await using var client = await ConnectAsync(transport);

            var creating = client.CreateSessionAsync(new SessionOptions { WorkingDirectory = Environment.CurrentDirectory }, Token);
            using var request = await transport.ReadSentAsync();
            transport.Respond(request, """{"sessionId":"session"}""");
            transport.Receive("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"session","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"early"}}}}""");
            var session = await creating.WaitAsync(TimeSpan.FromSeconds(5), Token);

            var collected = new List<SessionEvent>();
            var prompting = CollectAsync(session.PromptAsync("hello", Token), collected);
            using var prompt = await transport.ReadSentAsync();
            transport.Respond(prompt, """{"stopReason":"end_turn"}""");
            await prompting.WaitAsync(TimeSpan.FromSeconds(5), Token);

            var update = collected.OfType<SessionUpdateEvent>().SingleOrDefault();
            Assert.NotNull(update);
            var chunk = Assert.IsType<SessionUpdateVariant1AgentMessageChunk>(update!.Update.Value);
            var text = Assert.IsType<TextContent>(chunk.Content.Value);
            Assert.Equal("early", text.Text);
        }
    }

    [Fact]
    public async Task AMalformedNotificationDoesNotTerminateTheConnection()
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);

        // "params" is not an object, so the notification cannot be interpreted.
        transport.Receive("""{"jsonrpc":"2.0","method":"session/update","params":"not-an-object"}""");

        // The connection survives; a later exchange still works.
        using var barrier = await BarrierAsync(client, transport);
        Assert.False(client.Completion.IsCompleted);
    }

    [Fact]
    public async Task AMalformedNotificationForAKnownSessionDoesNotTerminateTheConnection()
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        _ = await CreateSessionAsync(transport, client);

        transport.Receive("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"session","update":"not-an-object"}}""");

        await BarrierAsync(client, transport);
        Assert.False(client.Completion.IsCompleted);
    }

    [Fact]
    public async Task AnEmptyInitializeResponseIsReportedAsAProtocolFailure()
    {
        await using var transport = new TestTransport();
        var connecting = AcpClient.ConnectAsync(transport, null, Token);
        using var request = await transport.ReadSentAsync();
        transport.Respond(request, "null");

        var exception = await Assert.ThrowsAsync<ProtocolException>(() => connecting.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Contains("initialize response", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMalformedPromptSuggestionsPayloadIsIgnoredWithoutBreakingTheSession()
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        var session = await CreateSessionAsync(transport, client);

        // The vendor extension has an unexpected shape.
        transport.Receive("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"session","update":{"sessionUpdate":"session_info_update","_meta":{"promptSuggestions":{"unexpected":true}}}}}""");

        await BarrierAsync(client, transport);
        Assert.Null(session.PromptSuggestions);
        Assert.False(client.Completion.IsCompleted);
    }

    [Fact]
    public async Task AMalformedAuthenticationStatusUpdateIsIgnoredWithoutBreakingTheConnection()
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);

        transport.Receive("""{"jsonrpc":"2.0","method":"_auth/status_update","params":{"authStatus":{"kind":"account"}}}""");

        await BarrierAsync(client, transport);
        Assert.Null(client.AuthenticationStatus);
        Assert.False(client.Completion.IsCompleted);
    }

    [Fact]
    public async Task AWellFormedAuthenticationStatusUpdateIsStillApplied()
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);

        transport.Receive("""{"jsonrpc":"2.0","method":"_auth/status_update","params":{"authStatus":{"kind":"account","label":"Plus"}}}""");

        await BarrierAsync(client, transport);
        var status = Assert.IsType<AuthenticationStatus>(client.AuthenticationStatus);
        Assert.Equal("account", status.Kind);
        Assert.Equal("Plus", status.Label);
    }

    [Fact]
    public async Task CloseSessionUsesTheCloseSessionRequestModel()
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport, agentResponse: """{"protocolVersion":1,"agentCapabilities":{"sessionCapabilities":{"close":{}}}}""");

        var closing = client.CloseSessionAsync("session-1", Token);
        using var request = await transport.ReadSentAsync();
        Assert.Equal("session/close", request.RootElement.GetProperty("method").GetString());
        Assert.Equal("session-1", request.RootElement.GetProperty("params").GetProperty("sessionId").GetString());
        transport.Respond(request, "{}");
        await closing.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    private static async Task<List<SessionEvent>> CollectAsync(IAsyncEnumerable<SessionEvent> events, List<SessionEvent> sink)
    {
        await foreach (var item in events.WithCancellation(Token)) sink.Add(item);
        return sink;
    }

    private static async Task<Session> CreateSessionAsync(TestTransport transport, AcpClient client)
    {
        var creating = client.CreateSessionAsync(new SessionOptions { WorkingDirectory = Environment.CurrentDirectory }, Token);
        using var request = await transport.ReadSentAsync();
        transport.Respond(request, """{"sessionId":"session"}""");
        return await creating.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    private static async Task<JsonDocument> BarrierAsync(AcpClient client, TestTransport transport)
    {
        var pending = client.SendExtensionRequestAsync("_test/barrier", Element("{}"), Token);
        using var request = await transport.ReadSentAsync();
        transport.Respond(request, "{}");
        await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
        return request;
    }

    private static async Task<AcpClient> ConnectAsync(
        TestTransport transport,
        ClientOptions? options = null,
        string? agentResponse = null)
    {
        var connecting = AcpClient.ConnectAsync(transport, options, Token);
        using var request = await transport.ReadSentAsync();
        transport.Respond(request, agentResponse ?? """{"protocolVersion":1,"agentCapabilities":{}}""");
        return await connecting.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
