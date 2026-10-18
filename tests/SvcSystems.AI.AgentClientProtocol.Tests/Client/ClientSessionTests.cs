using System.Text;
using System.Text.Json;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

public sealed class ClientSessionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("new")]
    [InlineData("load")]
    [InlineData("resume")]
    public async Task SessionAndPromptMetadataRoundTripWithoutLosingResponseMetadata(string operation)
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        var options = new SessionOptions
        {
            WorkingDirectory = Environment.CurrentDirectory,
            Metadata = Properties(Element("""{"customInstructions":"use the workspace","mcpStartupAwaitTimeoutMs":1000}"""))
        };
        var creating = operation switch
        {
            "new" => client.CreateSessionAsync(options, Token),
            "load" => client.LoadSessionAsync("session", options, Token),
            _ => client.ResumeSessionAsync("session", options, Token)
        };
        using var request = await transport.ReadSentAsync();
        Assert.Equal("session/" + operation, request.RootElement.GetProperty("method").GetString());
        Assert.Equal(1000, request.RootElement.GetProperty("params").GetProperty("_meta").GetProperty("mcpStartupAwaitTimeoutMs").GetInt32());
        transport.Respond(request, """{"sessionId":"session","_meta":{"sessionExtra":true}}""");
        var session = await creating.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(session.RawPayload!.Value.GetProperty("_meta").GetProperty("sessionExtra").GetBoolean());
        var prompting = CollectAsync(session.PromptAsync("hello", Token, new PromptOptions
        {
            Metadata = Properties(Element("""{"requestId":"turn-1","vendor":{"report":true}}"""))
        }));
        using var prompt = await transport.ReadSentAsync();
        var parameters = prompt.RootElement.GetProperty("params");
        Assert.Equal("hello", parameters.GetProperty("prompt")[0].GetProperty("text").GetString());
        Assert.Equal("turn-1", parameters.GetProperty("_meta").GetProperty("requestId").GetString());
        transport.Receive("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"session","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"reply"},"_meta":{"chunkExtra":123}}}}""");
        transport.Respond(prompt, """{"stopReason":"end_turn","_meta":{"quota":{"tokens":42}}}""");
        var updates = await prompting.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var update = Assert.IsType<SessionUpdateEvent>(updates[0]).Update.Value;
        Assert.Equal(123, Assert.IsType<SessionUpdateVariant1AgentMessageChunk>(update).Meta!["chunkExtra"].GetInt32());
        Assert.Equal(42, Assert.IsType<Completed>(updates[1]).Response.Meta!["quota"].GetProperty("tokens").GetInt32());
    }

    [Fact]
    public async Task PromptSuggestionsAreObservableWhileSessionIsIdleAndCanBeCleared()
    {
        await using var transport = new TestTransport();
        var changes = new List<(string SessionId, IReadOnlyList<string> Suggestions)>();
        await using var client = await ConnectAsync(transport, new ClientOptions
        {
            PromptSuggestionsChanged = (id, suggestions) => changes.Add((id, suggestions))
        });
        var creating = client.CreateSessionAsync(new SessionOptions { WorkingDirectory = Environment.CurrentDirectory }, Token);
        using var request = await transport.ReadSentAsync();
        transport.Respond(request, """{"sessionId":"session"}""");
        var session = await creating.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Null(session.PromptSuggestions);
        transport.Receive("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"session","update":{"sessionUpdate":"session_info_update","_meta":{"promptSuggestions":{"suggestions":[{"text":"Explain the change"}]}}}}}""");
        await BarrierAsync(client, transport);
        Assert.Equal("Explain the change", Assert.Single(session.PromptSuggestions!));
        Assert.Equal("session", Assert.Single(changes).SessionId);
        transport.Receive("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"session","update":{"sessionUpdate":"session_info_update","_meta":{"promptSuggestions":{"suggestions":[]}}}}}""");
        await BarrierAsync(client, transport);
        Assert.Empty(session.PromptSuggestions!);
        Assert.Empty(changes[1].Suggestions);
    }

    [Fact]
    public async Task DuplicateSessionIdsReleaseTheAgentSideSessionBeforeFailing()
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        var options = new SessionOptions { WorkingDirectory = Environment.CurrentDirectory };

        var creating = client.CreateSessionAsync(options, Token);
        using (var request = await transport.ReadSentAsync())
        {
            transport.Respond(request, """{"sessionId":"session"}""");
        }
        _ = await creating.WaitAsync(TimeSpan.FromSeconds(5), Token);

        var duplicate = client.CreateSessionAsync(options, Token);
        using (var request = await transport.ReadSentAsync())
        {
            transport.Respond(request, """{"sessionId":"session"}""");
        }
        using (var close = await transport.ReadSentAsync())
        {
            Assert.Equal("session/close", close.RootElement.GetProperty("method").GetString());
            Assert.Equal("session", close.RootElement.GetProperty("params").GetProperty("sessionId").GetString());
            transport.Respond(close, "{}");
        }

        var error = await Assert.ThrowsAsync<ProtocolException>(() => duplicate.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Contains("duplicate session id", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateBufferFailureCancelsWithoutStallingTheReceiveLoop()
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport, new ClientOptions { MaximumBufferedSessionUpdates = 1 });

        var creating = client.CreateSessionAsync(new SessionOptions { WorkingDirectory = Environment.CurrentDirectory }, Token);
        using (var request = await transport.ReadSentAsync())
        {
            transport.Respond(request, """{"sessionId":"session"}""");
        }
        _ = await creating.WaitAsync(TimeSpan.FromSeconds(5), Token);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendGate = message => Encoding.UTF8.GetString(message.Span).Contains("session/cancel", StringComparison.Ordinal)
            ? release.Task
            : Task.CompletedTask;

        try
        {
            transport.Receive(SessionInfoUpdate("first"));
            transport.Receive(SessionInfoUpdate("second"));
            await BarrierAsync(client, transport);
        }
        finally
        {
            release.SetResult();
        }

        using var cancel = await transport.ReadSentAsync();
        Assert.Equal("session/cancel", cancel.RootElement.GetProperty("method").GetString());
    }

    private static async Task<AcpClient> ConnectAsync(TestTransport transport, ClientOptions? options = null)
    {
        var connecting = AcpClient.ConnectAsync(transport, options, Token);
        using var request = await transport.ReadSentAsync();
        transport.Respond(request, """{"protocolVersion":1,"agentCapabilities":{"loadSession":true,"sessionCapabilities":{"resume":{}}}}""");
        return await connecting.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    private static async Task BarrierAsync(AcpClient client, TestTransport transport)
    {
        var pending = client.SendExtensionRequestAsync("_test/barrier", Element("{}"), Token);
        using var request = await transport.ReadSentAsync();
        transport.Respond(request, "{}");
        await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    private static async Task<List<SessionEvent>> CollectAsync(IAsyncEnumerable<SessionEvent> events)
    {
        var result = new List<SessionEvent>();
        await foreach (var item in events.WithCancellation(Token))
        {
            result.Add(item);
        }

        return result;
    }

    private static Dictionary<string, JsonElement> Properties(JsonElement element) =>
        element.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string SessionInfoUpdate(string text) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"session\",\"update\":{\"sessionUpdate\":\"session_info_update\",\"_meta\":{\"promptSuggestions\":{\"suggestions\":[{\"text\":"
        + QuoteJsonString(text) + "}]}}}}}";

    private static string QuoteJsonString(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";
}
