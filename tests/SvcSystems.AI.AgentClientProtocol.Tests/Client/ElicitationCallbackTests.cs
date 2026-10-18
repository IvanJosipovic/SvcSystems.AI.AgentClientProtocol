using System.Text.Json;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

public sealed class ElicitationCallbackTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ElicitationModesAreAdvertisedFromTheConfiguredHandler()
    {
        await using var bothTransport = new TestTransport();
        var (both, bothCapabilities) = await ConnectAsync(bothTransport, new ClientOptions
        {
            ElicitationHandler = new RecordingElicitationHandler { SupportsForm = true, SupportsUrl = true }
        });
        await using (both)
        {
            var elicitation = bothCapabilities.GetProperty("elicitation");
            Assert.Equal(JsonValueKind.Object, elicitation.GetProperty("form").ValueKind);
            Assert.Equal(JsonValueKind.Object, elicitation.GetProperty("url").ValueKind);
        }

        await using var formTransport = new TestTransport();
        var (form, formCapabilities) = await ConnectAsync(formTransport, new ClientOptions
        {
            ElicitationHandler = new RecordingElicitationHandler { SupportsForm = true }
        });
        await using (form)
        {
            var elicitation = formCapabilities.GetProperty("elicitation");
            Assert.Equal(JsonValueKind.Object, elicitation.GetProperty("form").ValueKind);
            Assert.False(elicitation.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.Object);
        }

        await using var bareTransport = new TestTransport();
        var (bare, bareCapabilities) = await ConnectAsync(bareTransport, new ClientOptions());
        await using (bare)
        {
            Assert.False(bareCapabilities.TryGetProperty("elicitation", out var elicitation) && elicitation.ValueKind == JsonValueKind.Object);
        }
    }

    [Fact]
    public async Task ElicitationRequestsAreRoutedToTheConfiguredHandler()
    {
        await using var transport = new TestTransport();
        var handler = new RecordingElicitationHandler { SupportsForm = true, SupportsUrl = true };
        var (client, _) = await ConnectAsync(transport, new ClientOptions { ElicitationHandler = handler });
        await using (client)
        {
            handler.Response = new ElicitationAcceptAction
            {
                Content = new Dictionary<string, ElicitationContentValue> { ["name"] = new ElicitationContentValue("Ada") }
            };
            transport.Receive(Request(
                "form",
                "elicitation/create",
                """{"mode":"form","message":"What is your name?","sessionId":"session-1","toolCallId":"tool-1","requestId":"request-1","requestedSchema":{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}}"""));
            using (var response = await transport.ReadSentAsync())
            {
                var request = Assert.IsType<ElicitationFormMode>(handler.LastRequest);
                Assert.Equal("What is your name?", request.Message);
                Assert.Equal("session-1", request.SessionId);
                Assert.Equal("tool-1", request.ToolCallId);
                Assert.Equal("request-1", RequestIdText(request.RequestId));
                Assert.True(request.RequestedSchema!.Properties!.ContainsKey("name"));
                Assert.Equal("name", Assert.Single(request.RequestedSchema.Required!));

                var result = response.RootElement.GetProperty("result");
                Assert.Equal("accept", result.GetProperty("action").GetString());
                Assert.Equal("Ada", result.GetProperty("content").GetProperty("name").GetString());
            }

            handler.Response = new CreateElicitationResponseCase1();
            transport.Receive(Request(
                "url",
                "elicitation/create",
                """{"mode":"url","message":"Sign in","sessionId":"session-1","elicitationId":"elicit-1","url":"https://example.test/login"}"""));
            using (var response = await transport.ReadSentAsync())
            {
                var request = Assert.IsType<ElicitationUrlMode>(handler.LastRequest);
                Assert.Equal("Sign in", request.Message);
                Assert.Equal("session-1", request.SessionId);
                Assert.Equal("elicit-1", request.ElicitationId);
                Assert.Equal("https://example.test/login", request.Url);

                var result = response.RootElement.GetProperty("result");
                Assert.Equal("decline", result.GetProperty("action").GetString());
                Assert.False(result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object);
            }

            handler.Response = new CreateElicitationResponseCase2();
            transport.Receive(Request(
                "form-again",
                "elicitation/create",
                """{"mode":"form","message":"Name?","sessionId":"session-1","requestedSchema":{"type":"object"}}"""));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.Equal("cancel", response.RootElement.GetProperty("result").GetProperty("action").GetString());
            }

            Assert.Equal(3, handler.RequestCount);
        }
    }

    [Fact]
    public async Task ElicitationRejectsUnsupportedModesBeforeCallingTheHandler()
    {
        await using var transport = new TestTransport();
        var handler = new RecordingElicitationHandler { SupportsForm = true };
        var (client, _) = await ConnectAsync(transport, new ClientOptions { ElicitationHandler = handler });
        await using (client)
        {
            transport.Receive(Request(
                "url",
                "elicitation/create",
                """{"mode":"url","message":"Sign in","sessionId":"session-1","url":"https://example.test/login"}"""));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.Equal(-32601, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            }

            Assert.Equal(0, handler.RequestCount);
        }
    }

    [Fact]
    public async Task ElicitationForwardsExtensionActionsAndRejectsUnknownOnes()
    {
        await using var transport = new TestTransport();
        var handler = new RecordingElicitationHandler
        {
            SupportsForm = true,
            Response = new CreateElicitationResponseCase3 { Action = "_custom" }
        };
        var (client, _) = await ConnectAsync(transport, new ClientOptions { ElicitationHandler = handler });
        await using (client)
        {
            transport.Receive(Request("extension", "elicitation/create", """{"mode":"form","message":"Name?","sessionId":"session-1","requestedSchema":{"type":"object"}}"""));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.Equal("_custom", response.RootElement.GetProperty("result").GetProperty("action").GetString());
            }

            handler.Response = new CreateElicitationResponseCase3 { Action = "bogus" };
            transport.Receive(Request("unknown", "elicitation/create", """{"mode":"form","message":"Name?","sessionId":"session-1","requestedSchema":{"type":"object"}}"""));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            }
        }
    }

    [Fact]
    public async Task ElicitationCompletionNotificationsReachTheCompletionCallback()
    {
        await using var transport = new TestTransport();
        CompleteElicitationNotification? completed = null;
        var handler = new RecordingElicitationHandler { SupportsForm = true, Response = new CreateElicitationResponseCase2() };
        var (client, _) = await ConnectAsync(transport, new ClientOptions
        {
            ElicitationHandler = handler,
            ElicitationCompleted = value => completed = value
        });
        await using (client)
        {
            transport.Receive("""{"jsonrpc":"2.0","method":"elicitation/complete","params":{"elicitationId":"elicit-1"}}""");
            // Notifications produce no response, so a following request acts as a barrier: the
            // receive loop dispatches in order, so the callback has run by the time it replies.
            transport.Receive(Request("barrier", "elicitation/create", """{"mode":"form","message":"Name?","sessionId":"session-1","requestedSchema":{"type":"object"}}"""));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.Equal("cancel", response.RootElement.GetProperty("result").GetProperty("action").GetString());
            }

            var notification = Assert.IsType<CompleteElicitationNotification>(completed);
            Assert.Equal("elicit-1", notification.ElicitationId);
        }
    }

    /// <summary>Unwraps the string form of a request-scoped elicitation identifier.</summary>
    private static string? RequestIdText(RequestId? requestId) => requestId is { } id ? id.Value as string : null;

    private static async Task<(AcpClient Client, JsonElement Capabilities)> ConnectAsync(TestTransport transport, ClientOptions options)
    {
        var connecting = AcpClient.ConnectAsync(transport, options, Token);
        using var initialize = await transport.ReadSentAsync();
        transport.Respond(initialize, """{"protocolVersion":1,"agentCapabilities":{}}""");
        var client = await connecting.WaitAsync(TimeSpan.FromSeconds(5), Token);
        return (client, initialize.RootElement.GetProperty("params").GetProperty("clientCapabilities").Clone());
    }

    private static string Request(string id, string method, string parameters) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + QuoteJsonString(id) + ",\"method\":" + QuoteJsonString(method) + ",\"params\":" + parameters + "}";

    private static string QuoteJsonString(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";

    private sealed class RecordingElicitationHandler : IElicitationHandler
    {
        private int _requestCount;

        public bool SupportsForm { get; init; }

        public bool SupportsUrl { get; init; }

        public CreateElicitationResponse Response { get; set; } = new ElicitationAcceptAction();

        public int RequestCount => Volatile.Read(ref _requestCount);

        /// <summary>The payload of the most recent request, taken from the union's <c>Value</c>.</summary>
        public object? LastRequest { get; private set; }

        public ValueTask<CreateElicitationResponse> RequestElicitationAsync(CreateElicitationRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _requestCount);
            LastRequest = request.Value;
            return ValueTask.FromResult(Response);
        }
    }
}
