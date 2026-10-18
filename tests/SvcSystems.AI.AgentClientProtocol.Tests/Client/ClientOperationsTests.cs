using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

public sealed class ClientOperationsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AdvertisedOperationsSendRequestsAndReturnResponses()
    {
        await using var transport = new TestTransport();
        const string agentResponse = """
            {
              "protocolVersion": 1,
              "agentCapabilities": {
                "auth": { "logout": {} },
                "sessionCapabilities": { "list": {}, "delete": {}, "close": {} }
              },
              "authMethods": [{ "id": "token", "name": "Token" }]
            }
            """;
        await using var client = await ConnectAsync(transport, agentResponse: agentResponse);

        var authentication = client.AuthenticateAsync("token", Token);
        using (var request = await transport.ReadSentAsync())
        {
            Assert.Equal("authenticate", request.RootElement.GetProperty("method").GetString());
            Assert.Equal("token", request.RootElement.GetProperty("params").GetProperty("methodId").GetString());
            transport.Respond(request, "{}");
        }
        await authentication;

        var logout = client.LogoutAsync(Token);
        using (var request = await transport.ReadSentAsync())
        {
            Assert.Equal("logout", request.RootElement.GetProperty("method").GetString());
            transport.Respond(request, "{}");
        }
        await logout;

        var listPage = client.ListSessionsPageAsync(Path.GetTempPath(), "cursor", Token);
        using (var request = await transport.ReadSentAsync())
        {
            Assert.Equal("session/list", request.RootElement.GetProperty("method").GetString());
            Assert.Equal("cursor", request.RootElement.GetProperty("params").GetProperty("cursor").GetString());
            transport.Respond(request, """{"sessions":[{"sessionId":"listed","cwd":"/tmp"}],"nextCursor":"next"}""");
        }
        var page = await listPage;
        Assert.Equal("listed", Assert.Single(page.Sessions).SessionId);
        Assert.Equal("next", page.NextCursor);

        var listProjection = client.ListSessionsAsync(cancellationToken: Token);
        using (var request = await transport.ReadSentAsync())
        {
            transport.Respond(request, """{"sessions":[{"sessionId":"projected","cwd":"/tmp"}]}""");
        }
        Assert.Equal("projected", Assert.Single(await listProjection).SessionId);

        var deletion = client.DeleteSessionAsync("deleted", Token);
        using (var request = await transport.ReadSentAsync())
        {
            Assert.Equal("session/delete", request.RootElement.GetProperty("method").GetString());
            transport.Respond(request, "{}");
        }
        await deletion;

        var close = client.CloseSessionAsync("closed", Token);
        using (var request = await transport.ReadSentAsync())
        {
            Assert.Equal("session/close", request.RootElement.GetProperty("method").GetString());
            transport.Respond(request, "{}");
        }
        await close;

        var modeChange = client.SetSessionModeAsync("session", "mode", Token);
        using (var request = await transport.ReadSentAsync())
        {
            Assert.Equal("session/set_mode", request.RootElement.GetProperty("method").GetString());
            transport.Respond(request, "{}");
        }
        await modeChange;

        var configChange = client.SetSessionConfigOptionAsync(
            new SetSessionConfigOptionRequest(
                new SetSessionConfigOptionRequestCase0 { SessionId = "session", ConfigId = "confirm", Value = true }),
            Token);
        using (var request = await transport.ReadSentAsync())
        {
            Assert.Equal("session/set_config_option", request.RootElement.GetProperty("method").GetString());
            transport.Respond(request, """{"configOptions":[]}""");
        }
        Assert.Empty(await configChange);
    }

    [Fact]
    public async Task TerminalAuthenticationRunsConfiguredCommandWithAgentOverrides()
    {
        await using var transport = new TestTransport();
        var terminal = new RecordingTerminalAuthenticationHandler();
        var processOptions = new ProcessOptions
        {
            FileName = "agent",
            Arguments = ["--acp"],
            WorkingDirectory = Path.GetTempPath(),
            Environment = new Dictionary<string, string?> { ["SHARED"] = "base", ["BASE_ONLY"] = "yes" },
        };
        const string agentResponse = """
            {
              "protocolVersion": 1,
              "authMethods": [{
                "type": "terminal",
                "id": "login",
                "name": "Login",
                "args": ["auth", "--interactive"],
                "env": { "SHARED": "terminal", "TERMINAL_ONLY": "yes" }
              }]
            }
            """;
        await using var client = await ConnectAsync(
            transport,
            new ClientOptions { TerminalAuthenticationHandler = terminal },
            agentResponse,
            processOptions);

        await client.AuthenticateAsync("login", Token);

        Assert.NotNull(terminal.Request);
        Assert.Equal("agent", terminal.Request.FileName);
        Assert.Equal(["--acp", "auth", "--interactive"], terminal.Request.Arguments);
        Assert.Equal(Path.GetTempPath(), terminal.Request.WorkingDirectory);
        Assert.Equal("terminal", terminal.Request.Environment["SHARED"]);
        Assert.Equal("yes", terminal.Request.Environment["BASE_ONLY"]);
        Assert.Equal("yes", terminal.Request.Environment["TERMINAL_ONLY"]);
    }

    [Fact]
    public async Task PermissionCallbackSelectsAnAdvertisedOption()
    {
        await using var transport = new TestTransport();
        var permissionHandler = new SelectingPermissionHandler("allow");
        await using var client = await ConnectAsync(transport, new ClientOptions { PermissionHandler = permissionHandler });
        transport.Receive("""
            {"jsonrpc":"2.0","id":41,"method":"session/request_permission","params":{
              "sessionId":"session","toolCall":{"toolCallId":"call","title":"Run tool"},
              "options":[{"optionId":"allow","name":"Allow","kind":"allow_once"}]}}
            """);

        using var response = await transport.ReadSentAsync();

        Assert.Equal(41, response.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("selected", response.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.Equal("allow", response.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
        Assert.Equal("session", permissionHandler.Request?.SessionId);
    }

    [Fact]
    public async Task PermissionCallbackFallsBackToRejectAndErrorsWhenNoneIsOffered()
    {
        await using var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);

        transport.Receive("""
            {"jsonrpc":"2.0","id":42,"method":"session/request_permission","params":{
              "sessionId":"session","toolCall":{"toolCallId":"call","title":"Run tool"},
              "options":[{"optionId":"reject","name":"Reject","kind":"reject_once"}]}}
            """);
        using (var rejected = await transport.ReadSentAsync())
        {
            Assert.Equal("reject", rejected.RootElement.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
        }

        transport.Receive("""
            {"jsonrpc":"2.0","id":43,"method":"session/request_permission","params":{
              "sessionId":"session","toolCall":{"toolCallId":"call","title":"Run tool"},
              "options":[{"optionId":"allow","name":"Allow","kind":"allow_once"}]}}
            """);
        using var response = await transport.ReadSentAsync();
        Assert.Equal(-32601, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    private static async Task<AcpClient> ConnectAsync(
        TestTransport transport,
        ClientOptions? options = null,
        string? agentResponse = null,
        ProcessOptions? terminalAuthenticationProcessOptions = null)
    {
        var connecting = AcpClient.ConnectAsync(transport, options, Token, terminalAuthenticationProcessOptions);
        using var request = await transport.ReadSentAsync();
        transport.Respond(request, agentResponse ?? """{"protocolVersion":1,"agentCapabilities":{"loadSession":true,"sessionCapabilities":{"resume":{}}}}""");
        return await connecting.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    private sealed class RecordingTerminalAuthenticationHandler : ITerminalAuthenticationHandler
    {
        public TerminalAuthenticationRequest? Request { get; private set; }

        public ValueTask<int> RunAsync(TerminalAuthenticationRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return ValueTask.FromResult(0);
        }
    }

    private sealed class SelectingPermissionHandler(string optionId) : IPermissionHandler
    {
        public RequestPermissionRequest? Request { get; private set; }

        public ValueTask<PermissionDecision> RequestPermissionAsync(
            RequestPermissionRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return ValueTask.FromResult(PermissionDecision.Select(optionId));
        }
    }
}
