using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SvcSystems.AI.AgentClientProtocol.Transport;
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

    [Fact]
    public async Task FileSystemCallbacksAreRestrictedToSessionWorkspaceRoots()
    {
        var workspace = Directory.CreateTempSubdirectory("acp-workspace-");
        var additional = Directory.CreateTempSubdirectory("acp-additional-");
        var outside = Directory.CreateTempSubdirectory("acp-outside-");
        try
        {
            await using var transport = new TestTransport();
            var handler = new RecordingFileSystemHandler();
            var connecting = AcpClient.ConnectAsync(transport, new ClientOptions { FileSystemHandler = handler }, Token);
            using (var initialize = await transport.ReadSentAsync())
            {
                transport.Respond(initialize, """{"protocolVersion":1,"agentCapabilities":{"sessionCapabilities":{"additionalDirectories":{}}}}""");
            }
            await using var client = await connecting.WaitAsync(TimeSpan.FromSeconds(5), Token);

            var creatingSession = client.CreateSessionAsync(new SessionOptions
            {
                WorkingDirectory = workspace.FullName,
                AdditionalDirectories = [additional.FullName]
            }, Token);
            using (var request = await transport.ReadSentAsync())
            {
                Assert.Equal("session/new", request.RootElement.GetProperty("method").GetString());
                transport.Respond(request, """{"sessionId":"session-1"}""");
            }
            _ = await creatingSession.WaitAsync(TimeSpan.FromSeconds(5), Token);

            var allowedPath = Path.Combine(additional.FullName, "allowed.txt");
            transport.Receive(FileSystemRequest("allowed-read", "fs/read_text_file", "session-1", allowedPath));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.Equal("allowed-read", response.RootElement.GetProperty("id").GetString());
                Assert.Equal(allowedPath, response.RootElement.GetProperty("result").GetProperty("content").GetString());
            }

            var traversalPath = Path.Combine(workspace.FullName, "..", Path.GetFileName(outside.FullName), "secret.txt");
            transport.Receive(FileSystemRequest("blocked-read", "fs/read_text_file", "session-1", traversalPath));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            }

            transport.Receive(FileSystemRequest("blocked-write", "fs/write_text_file", "session-1", Path.Combine(outside.FullName, "secret.txt")));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            }

            Assert.Equal(1, handler.ReadCount);
            Assert.Equal(0, handler.WriteCount);
        }
        finally
        {
            workspace.Delete(recursive: true);
            additional.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FileSystemCallbacksRejectSymlinkEscapes()
    {
        var workspace = Directory.CreateTempSubdirectory("acp-workspace-");
        var outside = Directory.CreateTempSubdirectory("acp-outside-");
        try
        {
            var linkPath = Path.Combine(workspace.FullName, "external");
            try
            {
                Directory.CreateSymbolicLink(linkPath, outside.FullName);
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                Assert.Skip("Creating symbolic links requires Developer Mode or symbolic-link privileges on Windows.");
            }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            {
                Assert.Skip("Creating symbolic links requires Developer Mode or symbolic-link privileges on Windows.");
            }

            await using var transport = new TestTransport();
            var handler = new RecordingFileSystemHandler();
            await using var client = await ConnectAsync(transport, new ClientOptions { FileSystemHandler = handler });

            var creatingSession = client.CreateSessionAsync(new SessionOptions { WorkingDirectory = workspace.FullName }, Token);
            using (var request = await transport.ReadSentAsync())
            {
                transport.Respond(request, """{"sessionId":"session-1"}""");
            }
            _ = await creatingSession.WaitAsync(TimeSpan.FromSeconds(5), Token);

            transport.Receive(FileSystemRequest("symlink-read", "fs/read_text_file", "session-1", Path.Combine(linkPath, "secret.txt")));
            using (var response = await transport.ReadSentAsync())
            {
                Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            }

            Assert.Equal(0, handler.ReadCount);
        }
        finally
        {
            workspace.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    private static string FileSystemRequest(string id, string method, string sessionId, string path)
    {
        var parameters = method == "fs/write_text_file"
            ? "{\"sessionId\":" + QuoteJsonString(sessionId) + ",\"path\":" + QuoteJsonString(path) + ",\"content\":\"data\"}"
            : "{\"sessionId\":" + QuoteJsonString(sessionId) + ",\"path\":" + QuoteJsonString(path) + "}";
        return "{\"jsonrpc\":\"2.0\",\"id\":" + QuoteJsonString(id) + ",\"method\":" + QuoteJsonString(method) + ",\"params\":" + parameters + "}";
    }

    private static string QuoteJsonString(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";

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
        await foreach (var item in events.WithCancellation(Token)) result.Add(item);
        return result;
    }

    private static Dictionary<string, JsonElement> Properties(JsonElement element) =>
        element.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class RecordingFileSystemHandler : IFileSystemHandler
    {
        private int _readCount;
        private int _writeCount;

        public int ReadCount => Volatile.Read(ref _readCount);
        public int WriteCount => Volatile.Read(ref _writeCount);

        public ValueTask<string> ReadTextFileAsync(ReadTextFileRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCount);
            return ValueTask.FromResult(request.Path);
        }

        public ValueTask WriteTextFileAsync(WriteTextFileRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestTransport : ITransport
    {
        private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly Channel<ReadOnlyMemory<byte>> _sent = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public void Receive(string json) => Assert.True(_incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(json)));
        public void Respond(JsonDocument request, string result) =>
            Receive($$"""{"jsonrpc":"2.0","id":{{request.RootElement.GetProperty("id").GetRawText()}},"result":{{result}}}""");
        public void RespondError(JsonDocument request, int code, string message) =>
            Receive($$$"""{"jsonrpc":"2.0","id":{{{request.RootElement.GetProperty("id").GetRawText()}}},"error":{"code":{{{code}}},"message":"{{{message}}}"}}""");
        public async Task<JsonDocument> ReadSentAsync() =>
            JsonDocument.Parse(await _sent.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) =>
            _sent.Writer.WriteAsync(message.ToArray(), cancellationToken);
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var message in _incoming.Reader.ReadAllAsync(cancellationToken)) yield return message;
        }
        public ValueTask DisposeAsync()
        {
            _incoming.Writer.TryComplete();
            _sent.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
