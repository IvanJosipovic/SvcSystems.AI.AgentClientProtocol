using System.Text.Json;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

public sealed class ExtensionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InitializationMetadataAndEarlyAuthenticationStatusArePreserved()
    {
        await using var transport = new TestTransport();
        var statuses = new List<AuthenticationStatus>();
        var notifications = new List<ExtensionNotification>();
        using var metadata = JsonDocument.Parse("""{"traceparent":"trace"}""");
        using var capability = JsonDocument.Parse("""{"vendor.example":{"version":1}}""");
        var connecting = AcpClient.ConnectAsync(transport, new ClientOptions
        {
            Metadata = Properties(metadata.RootElement),
            CapabilityMetadata = Properties(capability.RootElement),
            AuthenticationStatusChanged = statuses.Add,
            ExtensionNotificationReceived = notifications.Add
        }, Token);
        using var initialize = await transport.ReadSentAsync();
        var parameters = initialize.RootElement.GetProperty("params");
        Assert.Equal("trace", parameters.GetProperty("_meta").GetProperty("traceparent").GetString());
        Assert.Equal(1, parameters.GetProperty("clientCapabilities").GetProperty("_meta").GetProperty("vendor.example").GetProperty("version").GetInt32());
        Assert.False(parameters.GetProperty("clientCapabilities").GetProperty("terminal").GetBoolean());
        Assert.False(parameters.GetProperty("clientCapabilities").GetProperty("_meta").TryGetProperty("jetbrains", out _));

        transport.Receive("""
            {"jsonrpc":"2.0","method":"_auth/status_update","params":{"authStatus":{
              "kind":"account","label":"ChatGPT Plus","account":{"email":"user@example.com","organization":"org","plan":"plus"},
              "vendor":{"custom":true},"future":42}}}
            """);
        transport.Respond(initialize, """
            {"protocolVersion":1,"agentCapabilities":{"_meta":{"authStatus":{}}},
             "_meta":{"vendor.example":{"future":true}}}
            """);
        await using var client = await connecting.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(JsonValueKind.Object, client.Agent.AgentCapabilities!.Meta!["authStatus"].ValueKind);
        var status = Assert.IsType<AuthenticationStatus>(client.AuthenticationStatus);
        Assert.Same(status, Assert.Single(statuses));
        Assert.Equal("user@example.com", status.Account?.Email);
        Assert.Equal("org", status.Account?.Organization);
        Assert.Equal("plus", status.Account?.Plan);
        Assert.True(status.Vendor!.Value.GetProperty("custom").GetBoolean());
        Assert.Equal(42, status.RawPayload.GetProperty("authStatus").GetProperty("future").GetInt32());
        Assert.Equal("_auth/status_update", Assert.Single(notifications).Method);
        Assert.True(client.Agent.Meta!["vendor.example"].GetProperty("future").GetBoolean());
    }

    [Fact]
    public async Task AuthenticationStatusAllowsFutureKindsAndDistinguishesUnreportedFromLoggedOut()
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        Assert.Null(client.AuthenticationStatus);
        Assert.False(client.Agent.AgentCapabilities?.Meta?.ContainsKey("authStatus") ?? false);
        transport.Receive("""{"jsonrpc":"2.0","method":"_auth/status_update","params":{"authStatus":{"kind":"future","label":"New identity","detail":"provider"}}}""");
        await BarrierAsync(client, transport);
        Assert.Equal("future", client.AuthenticationStatus?.Kind);
        Assert.Equal("provider", client.AuthenticationStatus?.Detail);
        transport.Receive("""{"jsonrpc":"2.0","method":"_auth/status_update","params":{"authStatus":{"kind":"none","label":"Not logged in"}}}""");
        await BarrierAsync(client, transport);
        Assert.Equal("none", client.AuthenticationStatus?.Kind);
        Assert.Null(client.AuthenticationStatus?.Account);
    }

    [Fact]
    public async Task UnknownNotificationsAreObservableAndPayloadsOutliveCallbacks()
    {
        await using var transport = new TestTransport();
        var notifications = new List<ExtensionNotification>();
        await using var client = await ConnectAsync(transport, new ClientOptions { ExtensionNotificationReceived = notifications.Add });
        transport.Receive("""{"jsonrpc":"2.0","method":"_vendor/progress","params":{"_meta":{"vendor":{"nested":[1,2]}}}}""");
        await BarrierAsync(client, transport);
        await client.DisposeAsync();
        var notification = Assert.Single(notifications);
        Assert.Equal("_vendor/progress", notification.Method);
        Assert.Equal(2, notification.Parameters.GetProperty("_meta").GetProperty("vendor").GetProperty("nested")[1].GetInt32());
    }

    [Theory]
    [InlineData("_vendor/request")]
    [InlineData("providers/list")]
    public async Task ExtensionRequestsPreserveParametersAndFullResults(string method)
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        var sending = client.SendExtensionRequestAsync(method, Element("""{"_meta":{"vendor":{"enabled":true}},"items":[1,2]}"""), Token);
        using var request = await transport.ReadSentAsync();
        Assert.Equal(method, request.RootElement.GetProperty("method").GetString());
        Assert.True(request.RootElement.GetProperty("params").GetProperty("_meta").GetProperty("vendor").GetProperty("enabled").GetBoolean());
        transport.Respond(request, """{"answer":[42],"_meta":{"future":"retained"}}""");
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(42, result.GetProperty("answer")[0].GetInt32());
        Assert.Equal("retained", result.GetProperty("_meta").GetProperty("future").GetString());
    }

    [Fact]
    public async Task ExtensionNotificationsDoNotCreateRequests()
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        await client.SendExtensionNotificationAsync("_vendor/event", Element("[1,2]"), Token);
        using var notification = await transport.ReadSentAsync();
        Assert.Equal("_vendor/event", notification.RootElement.GetProperty("method").GetString());
        Assert.False(notification.RootElement.TryGetProperty("id", out _));
        Assert.Equal(2, notification.RootElement.GetProperty("params")[1].GetInt32());
    }

    [Fact]
    public async Task ExtensionRequestsPropagateRpcErrorsAndCancellation()
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        var failing = client.SendExtensionRequestAsync("_vendor/error", Element("{}"), Token);
        using var request = await transport.ReadSentAsync();
        transport.RespondError(request, -32001, "refused");
        var error = await Assert.ThrowsAsync<RpcException>(() => failing);
        Assert.Equal(-32001, error.Code);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var cancelled = client.SendExtensionRequestAsync("_vendor/wait", Element("{}"), cancellation.Token);
        using var waitingRequest = await transport.ReadSentAsync();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        using var cancellationNotification = await transport.ReadSentAsync();
        Assert.Equal("$/cancel_request", cancellationNotification.RootElement.GetProperty("method").GetString());
        Assert.Equal(waitingRequest.RootElement.GetProperty("id").GetInt64(),
            cancellationNotification.RootElement.GetProperty("params").GetProperty("id").GetInt64());
        await BarrierAsync(client, transport);
    }

    [Fact]
    public async Task IncomingExtensionRequestsUseConfiguredHandlersAndReturnErrors()
    {
        await using var transport = new TestTransport();
        JsonElement retained = default;
        var handlerCancellation = default(CancellationToken);
        var reportedException = new TaskCompletionSource<(string Method, Exception Exception)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = await ConnectAsync(transport, new ClientOptions
        {
            RequestHandlerException = (method, exception) => reportedException.TrySetResult((method, exception)),
            ExtensionRequestHandlers = new Dictionary<string, Func<JsonElement, CancellationToken, ValueTask<JsonElement>>>
            {
                ["_vendor/echo"] = (parameters, cancellation) =>
                {
                    retained = parameters;
                    handlerCancellation = cancellation;
                    return ValueTask.FromResult(parameters);
                },
                ["_vendor/error"] = (_, _) => throw new RpcException(-32042, "denied"),
                ["_vendor/bug"] = (_, _) => throw new InvalidOperationException("handler failed")
            }
        });
        transport.Receive("""{"jsonrpc":"2.0","id":"string-id","method":"_vendor/echo","params":{"_meta":{"custom":true}}}""");
        using var echo = await transport.ReadSentAsync();
        Assert.Equal("string-id", echo.RootElement.GetProperty("id").GetString());
        Assert.True(echo.RootElement.GetProperty("result").GetProperty("_meta").GetProperty("custom").GetBoolean());
        Assert.True(retained.GetProperty("_meta").GetProperty("custom").GetBoolean());
        Assert.True(handlerCancellation.CanBeCanceled);

        foreach (var (method, code) in new[] { ("_vendor/error", -32042), ("_vendor/bug", -32603), ("_vendor/missing", -32601), ("fs/read_text_file", -32601) })
        {
            transport.Receive($$$"""{"jsonrpc":"2.0","id":9,"method":"{{{method}}}","params":{}}""");
            using var response = await transport.ReadSentAsync();
            Assert.Equal(code, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            if (method == "_vendor/bug")
            {
                Assert.Equal("Internal error while handling the request.", response.RootElement.GetProperty("error").GetProperty("message").GetString());
                Assert.DoesNotContain("handler failed", response.RootElement.GetRawText(), StringComparison.Ordinal);
            }
        }
        var diagnostic = await reportedException.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal("_vendor/bug", diagnostic.Method);
        Assert.Equal("handler failed", diagnostic.Exception.Message);
        transport.Receive("""{"jsonrpc":"2.0","id":10,"method":"_vendor/echo","params":true}""");
        using var invalid = await transport.ReadSentAsync();
        Assert.Equal(-32602, invalid.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        await client.DisposeAsync();
        Assert.True(handlerCancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task IncomingExtensionHandlerCanAwaitAnOutgoingRequest()
    {
        await using var transport = new TestTransport();
        AcpClient? connected = null;
        await using var client = await ConnectAsync(transport, new ClientOptions
        {
            ExtensionRequestHandlers = new Dictionary<string, Func<JsonElement, CancellationToken, ValueTask<JsonElement>>>
            {
                ["_vendor/nested"] = async (parameters, cancellationToken) =>
                {
                    var current = connected ?? throw new InvalidOperationException("Not connected.");
                    return await current.SendExtensionRequestAsync("_vendor/lookup", parameters, cancellationToken);
                }
            }
        });
        connected = client;
        transport.Receive("""{"jsonrpc":"2.0","id":"outer","method":"_vendor/nested","params":{"key":"value"}}""");
        using var nested = await transport.ReadSentAsync();
        Assert.Equal("_vendor/lookup", nested.RootElement.GetProperty("method").GetString());
        transport.Respond(nested, """{"found":true}""");
        using var response = await transport.ReadSentAsync();
        Assert.Equal("outer", response.RootElement.GetProperty("id").GetString());
        Assert.True(response.RootElement.GetProperty("result").GetProperty("found").GetBoolean());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("[1,2]")]
    public async Task ExtensionHandlersCanReturnAnyJsonValue(string json)
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport, new ClientOptions
        {
            ExtensionRequestHandlers = new Dictionary<string, Func<JsonElement, CancellationToken, ValueTask<JsonElement>>>
            {
                ["_vendor/result"] = (_, _) => ValueTask.FromResult(Element(json))
            }
        });
        transport.Receive("""{"jsonrpc":"2.0","id":1,"method":"_vendor/result","params":{}}""");
        using var response = await transport.ReadSentAsync();
        Assert.Equal(json, response.RootElement.GetProperty("result").GetRawText());
    }

    [Theory]
    [InlineData("""{"authStatus":null}""")]
    [InlineData("""{"authStatus":{"kind":42,"label":"bad"}}""")]
    [InlineData("""{"authStatus":{"kind":"account","label":"bad","account":42}}""")]
    public async Task InvalidAuthenticationStatusIsReportedAsProtocolFailure(string parameters)
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        transport.Receive($$"""{"jsonrpc":"2.0","method":"_auth/status_update","params":{{parameters}}}""");
        Assert.IsType<ProtocolException>(await client.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Null(client.AuthenticationStatus);
    }

    [Fact]
    public async Task NotificationCallbackFailuresAreSurfaced()
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport, new ClientOptions
        {
            ExtensionNotificationReceived = _ => throw new InvalidOperationException("observer failed")
        });
        transport.Receive("""{"jsonrpc":"2.0","method":"_vendor/notification","params":{}}""");
        var error = Assert.IsType<InvalidOperationException>(await client.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Equal("observer failed", error.Message);
    }

    [Fact]
    public async Task InvalidParametersAndDisposedClientsFailExplicitly()
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendExtensionRequestAsync(" ", Element("{}"), Token));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendExtensionRequestAsync("_vendor/request", Element("null"), Token));
        await Assert.ThrowsAsync<ArgumentException>(async () => await client.SendExtensionNotificationAsync("_vendor/event", Element("1"), Token));
        await client.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SendExtensionRequestAsync("_vendor/request", Element("{}"), Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await client.SendExtensionNotificationAsync("_vendor/event", Element("{}"), Token));
    }

    [Fact]
    public async Task IncomingHandlersCannotOverrideStandardCallbacks()
    {
        await using var transport = new TestTransport();
        await Assert.ThrowsAsync<ArgumentException>(() => AcpClient.ConnectAsync(transport, new ClientOptions
        {
            ExtensionRequestHandlers = new Dictionary<string, Func<JsonElement, CancellationToken, ValueTask<JsonElement>>>
            {
                ["fs/read_text_file"] = (_, _) => ValueTask.FromResult(Element("{}"))
            }
        }, Token));
    }

    private static async Task<AcpClient> ConnectAsync(TestTransport transport, ClientOptions? options = null, string? agentResponse = null)
    {
        var connecting = AcpClient.ConnectAsync(transport, options, Token);
        using var request = await transport.ReadSentAsync();
        transport.Respond(request, agentResponse ?? """{"protocolVersion":1,"agentCapabilities":{"loadSession":true,"sessionCapabilities":{"resume":{}}}}""");
        return await connecting.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    private static async Task BarrierAsync(AcpClient client, TestTransport transport)
    {
        var pending = client.SendExtensionRequestAsync("_test/barrier", Element("{}"), Token);
        using var request = await transport.ReadSentAsync();
        transport.Respond(request, "{}");
        await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    private static Dictionary<string, JsonElement> Properties(JsonElement element) =>
        element.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }


}
