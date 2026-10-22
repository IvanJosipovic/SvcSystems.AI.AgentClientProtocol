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

    [Theory]
    [InlineData("image")]
    [InlineData("audio")]
    [InlineData("resource")]
    public async Task PromptRejectsContentTheAgentDoesNotAdvertise(string kind)
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        var creating = client.CreateSessionAsync(new SessionOptions { WorkingDirectory = Environment.CurrentDirectory }, Token);
        using (var request = await transport.ReadSentAsync())
        {
            transport.Respond(request, """{"sessionId":"session"}""");
        }
        var session = await creating.WaitAsync(TimeSpan.FromSeconds(5), Token);
        ContentBlock block = kind switch
        {
            "image" => new ImageContent { Data = "AAAA", MimeType = "image/png" },
            "audio" => new AudioContent { Data = "AAAA", MimeType = "audio/wav" },
            _ => new EmbeddedResource
            {
                Resource = new TextResourceContents { Uri = "file:///workspace/notes.txt", Text = "notes" }
            }
        };

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => CollectAsync(session.PromptAsync([block], Token)));

        Assert.Contains(kind == "resource" ? "embedded context" : kind, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadSessionFailureReleasesTheSessionForRetry()
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        var options = new SessionOptions { WorkingDirectory = Environment.CurrentDirectory };

        var loading = client.LoadSessionAsync("session", options, Token);
        using (var request = await transport.ReadSentAsync())
        {
            Assert.Equal("session/load", request.RootElement.GetProperty("method").GetString());
            transport.RespondError(request, -32603, "load failed");
        }

        await Assert.ThrowsAsync<RpcException>(() => loading.WaitAsync(TimeSpan.FromSeconds(5), Token));
    }

    [Fact]
    public async Task DuplicateSessionIdStillFailsWhenCleanupFails()
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
            transport.RespondError(close, -32603, "close failed");
        }

        var error = await Assert.ThrowsAsync<ProtocolException>(() => duplicate.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Contains("duplicate session id", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateBufferFailureToleratesCancellationTransportFailures()
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport, new ClientOptions { MaximumBufferedSessionUpdates = 1 });

        var creating = client.CreateSessionAsync(new SessionOptions { WorkingDirectory = Environment.CurrentDirectory }, Token);
        using (var request = await transport.ReadSentAsync())
        {
            transport.Respond(request, """{"sessionId":"session"}""");
        }
        _ = await creating.WaitAsync(TimeSpan.FromSeconds(5), Token);

        transport.SendGate = message => Encoding.UTF8.GetString(message.Span).Contains("session/cancel", StringComparison.Ordinal)
            ? Task.FromException(new IOException("transport failure"))
            : Task.CompletedTask;

        transport.Receive(SessionInfoUpdate("first"));
        transport.Receive(SessionInfoUpdate("second"));
        await BarrierAsync(client, transport);
    }

    [Fact]
    public async Task PermissionCallbackIsCancelledWhenThePromptIsCancelled()
    {
        await using var transport = new TestTransport();
        var handler = new WaitingPermissionHandler();
        await using var client = await ConnectAsync(transport, new ClientOptions { PermissionHandler = handler });
        var creating = client.CreateSessionAsync(new SessionOptions { WorkingDirectory = Environment.CurrentDirectory }, Token);
        using (var request = await transport.ReadSentAsync())
        {
            transport.Respond(request, """{"sessionId":"session"}""");
        }
        var session = await creating.WaitAsync(TimeSpan.FromSeconds(5), Token);

        var prompting = CollectAsync(session.PromptAsync("hello", Token));
        using (var prompt = await transport.ReadSentAsync())
        {
            Assert.Equal("session/prompt", prompt.RootElement.GetProperty("method").GetString());

            transport.Receive(PermissionRequest(41));
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

            var cancelling = session.CancelAsync(Token);
            using (var cancel = await transport.ReadSentAsync())
            {
                Assert.Equal("session/cancel", cancel.RootElement.GetProperty("method").GetString());
            }
            await cancelling.WaitAsync(TimeSpan.FromSeconds(5), Token);

            using (var cancelled = await transport.ReadSentAsync())
            {
                Assert.Equal(41, cancelled.RootElement.GetProperty("id").GetInt32());
                Assert.Equal("cancelled", cancelled.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
            }

            transport.Receive(PermissionRequest(42));
            using (var alreadyCancelled = await transport.ReadSentAsync())
            {
                Assert.Equal(42, alreadyCancelled.RootElement.GetProperty("id").GetInt32());
                Assert.Equal("cancelled", alreadyCancelled.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
            }

            transport.Respond(prompt, """{"stopReason":"cancelled"}""");
        }

        await prompting.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    private static string PermissionRequest(int id) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id
        + ",\"method\":\"session/request_permission\",\"params\":{\"sessionId\":\"session\",\"toolCall\":{\"toolCallId\":\"call\",\"title\":\"Run tool\"},\"options\":[{\"optionId\":\"allow\",\"name\":\"Allow\",\"kind\":\"allow_once\"}]}}";

    [Fact]
    public async Task SessionCancelIsWrittenBeforeThePendingPermissionIsAnswered()
    {
        // The ACP spec requires the client to cancel the prompt turn with session/cancel and only
        // then answer a pending session/request_permission with a cancelled outcome. Releasing the
        // permission handler first lets the permission response reach the wire before the
        // notification, so the agent is told about a decision for a turn it was never told to
        // cancel. This test pins the order of the two outbound frames.
        await using var transport = new TestTransport();
        var handler = new WaitingPermissionHandler();
        await using var client = await ConnectAsync(transport, new ClientOptions { PermissionHandler = handler });
        var creating = client.CreateSessionAsync(new SessionOptions { WorkingDirectory = Environment.CurrentDirectory }, Token);
        using (var request = await transport.ReadSentAsync())
        {
            transport.Respond(request, """{"sessionId":"session"}""");
        }
        var session = await creating.WaitAsync(TimeSpan.FromSeconds(5), Token);

        var prompting = CollectAsync(session.PromptAsync("hello", Token));
        using (var prompt = await transport.ReadSentAsync())
        {
            transport.Receive(PermissionRequest(41));
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

            var cancelling = session.CancelAsync(Token);

            // The notification must be the first frame written after the cancel is requested.
            using (var cancel = await transport.ReadSentAsync())
            {
                Assert.Equal("session/cancel", cancel.RootElement.GetProperty("method").GetString());
            }
            await cancelling.WaitAsync(TimeSpan.FromSeconds(5), Token);

            // The permission answer follows, and it is a cancelled outcome.
            using (var cancelled = await transport.ReadSentAsync())
            {
                Assert.Equal(41, cancelled.RootElement.GetProperty("id").GetInt32());
                Assert.Equal("cancelled", cancelled.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
            }

            transport.Respond(prompt, """{"stopReason":"cancelled"}""");
        }

        await prompting.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    private sealed class WaitingPermissionHandler : IPermissionHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<PermissionDecision> RequestPermissionAsync(
            RequestPermissionRequest request,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return PermissionDecision.Select(request.Options[0].OptionId);
        }
    }

    [Fact]
    public async Task SessionCancelAlwaysPrecedesTheCancelledPermissionAnswerUnderRepetition()
    {
        // The two outbound frames race for the same write path. Repeating the exchange catches an
        // ordering that only happens to hold on a quiet scheduler.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await using var transport = new TestTransport();
            var handler = new WaitingPermissionHandler();
            await using var client = await ConnectAsync(transport, new ClientOptions { PermissionHandler = handler });
            var creating = client.CreateSessionAsync(new SessionOptions { WorkingDirectory = Environment.CurrentDirectory }, Token);
            using (var request = await transport.ReadSentAsync())
            {
                transport.Respond(request, """{"sessionId":"session"}""");
            }
            var session = await creating.WaitAsync(TimeSpan.FromSeconds(5), Token);

            var prompting = CollectAsync(session.PromptAsync("hello", Token));
            using (var prompt = await transport.ReadSentAsync())
            {
                transport.Receive(PermissionRequest(41));
                await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

                var cancelling = session.CancelAsync(Token);
                using (var cancel = await transport.ReadSentAsync())
                {
                    Assert.Equal("session/cancel", cancel.RootElement.GetProperty("method").GetString());
                }
                await cancelling.WaitAsync(TimeSpan.FromSeconds(5), Token);

                using (var cancelled = await transport.ReadSentAsync())
                {
                    Assert.Equal(41, cancelled.RootElement.GetProperty("id").GetInt32());
                    Assert.Equal("cancelled", cancelled.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
                }

                transport.Respond(prompt, """{"stopReason":"cancelled"}""");
            }

            await prompting.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
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
