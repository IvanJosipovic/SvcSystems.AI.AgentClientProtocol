using System.Text.Json;
using SvcSystems.AI.AgentClientProtocol.Generated;
using SvcSystems.AI.AgentClientProtocol;
using SvcSystems.AI.AgentClientProtocol.Serialization;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

public sealed class WireJsonTests
{
    [Fact]
    public void UnambiguousSchemaUnionUsesSystemTextJsonSourceGeneratedMetadata()
    {
        EmbeddedResourceResource resource = new TextResourceContents
        {
            Uri = "file:///readme.txt",
            Text = "hello"
        };

        using var serialized = JsonDocument.Parse(GeneratedJson.Serialize(resource));
        Assert.Equal("file:///readme.txt", serialized.RootElement.GetProperty("uri").GetString());
        Assert.Equal("hello", serialized.RootElement.GetProperty("text").GetString());

        var roundTripped = GeneratedJson.DeserializeEmbeddedResourceResource(serialized.RootElement);
        using var serializedAgain = JsonDocument.Parse(GeneratedJson.Serialize(roundTripped));
        Assert.Equal(serialized.RootElement.GetProperty("text").GetString(), serializedAgain.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void SourceGeneratedUnionsRoundTripEveryDistinctJsonShape()
    {
        RequestId numericId = 42L;
        AssertGeneratedUnionRoundTrip<RequestId>("42", GeneratedJson.Serialize(numericId), GeneratedJson.Serialize, GeneratedJson.DeserializeRequestId);

        RequestId stringId = "request-1";
        AssertGeneratedUnionRoundTrip<RequestId>("\"request-1\"", GeneratedJson.Serialize(stringId), GeneratedJson.Serialize, GeneratedJson.DeserializeRequestId);

        EmbeddedResourceResource textResource = new TextResourceContents { Uri = "file:///text", Text = "body" };
        AssertGeneratedUnionRoundTrip<EmbeddedResourceResource>(
            "{\"uri\":\"file:///text\",\"text\":\"body\"}",
            GeneratedJson.Serialize(textResource),
            GeneratedJson.Serialize,
            GeneratedJson.DeserializeEmbeddedResourceResource);

        EmbeddedResourceResource blobResource = new BlobResourceContents { Uri = "file:///blob", Blob = "AAE=", MimeType = "application/octet-stream" };
        AssertGeneratedUnionRoundTrip<EmbeddedResourceResource>(
            "{\"uri\":\"file:///blob\",\"blob\":\"AAE=\",\"mimeType\":\"application/octet-stream\"}",
            GeneratedJson.Serialize(blobResource),
            GeneratedJson.Serialize,
            GeneratedJson.DeserializeEmbeddedResourceResource);

        ElicitationFormMode sessionMode = new ElicitationSessionScope { SessionId = "session-1", ToolCallId = "tool-1" };
        AssertGeneratedUnionRoundTrip<ElicitationFormMode>(
            "{\"sessionId\":\"session-1\",\"toolCallId\":\"tool-1\"}",
            GeneratedJson.Serialize(sessionMode),
            GeneratedJson.Serialize,
            GeneratedJson.DeserializeElicitationFormMode);

        ElicitationFormMode requestMode = new ElicitationRequestScope { RequestId = stringId };
        AssertGeneratedUnionRoundTrip<ElicitationFormMode>(
            "{\"requestId\":\"request-1\"}",
            GeneratedJson.Serialize(requestMode),
            GeneratedJson.Serialize,
            GeneratedJson.DeserializeElicitationFormMode);

        MultiSelectItems stringItems = new StringMultiSelectItems { Enum = ["red", "blue"] };
        AssertGeneratedUnionRoundTrip<MultiSelectItems>(
            "{\"enum\":[\"red\",\"blue\"]}",
            GeneratedJson.Serialize(stringItems),
            GeneratedJson.Serialize,
            GeneratedJson.DeserializeMultiSelectItems);

        MultiSelectItems titledItems = new TitledMultiSelectItems
        {
            AnyOf = [new EnumOption { Const = "red", Title = "Red" }]
        };
        AssertGeneratedUnionRoundTrip<MultiSelectItems>(
            "{\"anyOf\":[{\"const\":\"red\",\"title\":\"Red\"}]}",
            GeneratedJson.Serialize(titledItems),
            GeneratedJson.Serialize,
            GeneratedJson.DeserializeMultiSelectItems);

        MultiSelectItems typedItems = new MultiSelectItemsCase1 { Type = "dynamic" };
        AssertGeneratedUnionRoundTrip<MultiSelectItems>(
            "{\"type\":\"dynamic\"}",
            GeneratedJson.Serialize(typedItems),
            GeneratedJson.Serialize,
            GeneratedJson.DeserializeMultiSelectItems);

        ElicitationUrlMode urlMode = new ElicitationRequestScope { RequestId = numericId };
        AssertGeneratedUnionRoundTrip<ElicitationUrlMode>(
            "{\"requestId\":42}",
            GeneratedJson.Serialize(urlMode),
            GeneratedJson.Serialize,
            GeneratedJson.DeserializeElicitationUrlMode);

        CreateElicitationRequestCase2 scopedRequest = new ElicitationRequestScope { RequestId = stringId };
        AssertGeneratedUnionRoundTrip<CreateElicitationRequestCase2>(
            "{\"requestId\":\"request-1\"}",
            GeneratedJson.Serialize(scopedRequest),
            GeneratedJson.Serialize,
            GeneratedJson.DeserializeCreateElicitationRequestCase2);
    }

    [Fact]
    public void AmbiguousUnionFallbacksPreserveEachSelectedWireShape()
    {
        AssertUnionRoundTrip<SessionConfigSelectOptions>(
            "[{\"value\":\"fast\",\"name\":\"Fast\"}]",
            GeneratedJson.DeserializeSessionConfigSelectOptions,
            GeneratedJson.Serialize);
        AssertUnionRoundTrip<SessionConfigSelectOptions>(
            "[{\"group\":\"recommended\",\"name\":\"Recommended\",\"options\":[{\"value\":\"fast\",\"name\":\"Fast\"}]}]",
            GeneratedJson.DeserializeSessionConfigSelectOptions,
            GeneratedJson.Serialize);

        AssertUnionRoundTrip<ElicitationPropertySchema>(
            "{\"type\":\"string\",\"title\":\"Name\",\"minLength\":1}",
            GeneratedJson.DeserializeElicitationPropertySchema,
            GeneratedJson.Serialize);
        AssertUnionRoundTrip<ElicitationPropertySchema>(
            "{\"type\":\"integer\",\"title\":\"Count\",\"minimum\":1}",
            GeneratedJson.DeserializeElicitationPropertySchema,
            GeneratedJson.Serialize);
    }

    [Fact]
    public void TaggedMcpServerHierarchyDeserializesEachDiscriminator()
    {
        AssertMcpServer("""{"type":"stdio","name":"local","command":"agent","args":["--stdio"],"env":[]}""", typeof(McpServerStdio));
        AssertMcpServer("""{"type":"http","name":"remote","url":"https://example.test/mcp","headers":[]}""", typeof(McpServerHttp));
        AssertMcpServer("""{"type":"sse","name":"events","url":"https://example.test/events","headers":[]}""", typeof(McpServerSse));
    }

    [Fact]
    public void InitializeUsesGeneratedRequestModelAndAdvertisesOnlyConfiguredCapabilities()
    {
        var request = new InitializeRequest
        {
            ProtocolVersion = 1,
            ClientCapabilities = new ClientCapabilities
            {
                Fs = new FileSystemCapabilities { ReadTextFile = false, WriteTextFile = false },
                Auth = new AuthCapabilities { Terminal = false }
            }
        };
        var options = new ClientOptions
        {
            ClientInfo = new Implementation { Name = "wire-test", Version = "1.2.3" },
            SupportsCompactionUpdates = false,
            SupportsNoticeUpdates = false,
            SupportsBooleanSessionConfigOptions = false
        };

        using var document = JsonDocument.Parse(WireJson.WriteInitializeRequest(request, options.ClientInfo, options));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("wire-test", root.GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.False(root.GetProperty("clientCapabilities").GetProperty("fs").GetProperty("readTextFile").GetBoolean());
        Assert.False(root.GetProperty("clientCapabilities").GetProperty("terminal").GetBoolean());
        Assert.False(root.GetProperty("clientCapabilities").TryGetProperty("elicitation", out _));
        Assert.False(root.GetProperty("clientCapabilities").TryGetProperty("session", out _));
    }

    [Fact]
    public void PromptWriterPreservesKnownContentDiscriminators()
    {
        using var document = JsonDocument.Parse(WireJson.WritePromptRequest(
            "session-1",
            [
                new PromptTextContent("hello"),
                new ResourceLinkContent("source", "file:///workspace/source.txt", "desc", "text/plain")
            ]));

        var prompt = document.RootElement.GetProperty("prompt");
        Assert.Equal("text", prompt[0].GetProperty("type").GetString());
        Assert.Equal("hello", prompt[0].GetProperty("text").GetString());
        Assert.Equal("resource_link", prompt[1].GetProperty("type").GetString());
        Assert.Equal("file:///workspace/source.txt", prompt[1].GetProperty("uri").GetString());
    }

    [Fact]
    public void PromptWriterCoversContentVariantsAndOmitsAbsentOptionalFields()
    {
        using var document = JsonDocument.Parse(WireJson.WritePromptRequest("s", [
            new PromptTextContent("text"),
            new PromptImageContent("image-data", "image/png"),
            new PromptAudioContent("audio-data", "audio/wav"),
            new EmbeddedResourceContent("file:///text", "body", null, "text/plain"),
            new EmbeddedResourceContent("file:///blob", null, "base64", "application/octet-stream")
        ]));

        var prompt = document.RootElement.GetProperty("prompt");
        Assert.Equal(new[] { "text", "image", "audio", "resource", "resource" }, prompt.EnumerateArray()
            .Select(item => item.GetProperty("type").GetString()).ToArray());
        Assert.False(prompt[1].TryGetProperty("uri", out _));
        Assert.Equal("body", prompt[3].GetProperty("resource").GetProperty("text").GetString());
        Assert.Equal("base64", prompt[4].GetProperty("resource").GetProperty("blob").GetString());
    }

    [Fact]
    public void SessionWriterCoversAllMcpServerVariants()
    {
        var request = new NewSessionRequest { Cwd = "C:/workspace", McpServers = [] };
        var servers = new McpServerOptions[]
        {
            new StdioMcpServerOptions("stdio", "C:/tools/server.exe", ["--quiet"], new Dictionary<string, string> { ["MODE"] = "test" }),
            new HttpMcpServerOptions("http", "https://example.test/mcp", new Dictionary<string, string> { ["Authorization"] = "Bearer token" }),
            new SseMcpServerOptions("sse", "https://example.test/events")
        };

        using var document = JsonDocument.Parse(WireJson.WriteNewSessionRequest(request, servers));
        var mcp = document.RootElement.GetProperty("mcpServers");

        Assert.Equal(new[] { "stdio", "http", "sse" }, mcp.EnumerateArray().Select(item => item.GetProperty("type").GetString()).ToArray());
        Assert.Equal("--quiet", mcp[0].GetProperty("args")[0].GetString());
        Assert.Equal("MODE", mcp[0].GetProperty("env")[0].GetProperty("name").GetString());
        Assert.Equal("Authorization", mcp[1].GetProperty("headers")[0].GetProperty("name").GetString());
        Assert.Empty(mcp[2].GetProperty("headers").EnumerateArray());
        Assert.False(document.RootElement.TryGetProperty("additionalDirectories", out _));
    }

    [Fact]
    public void CallbackWritersPreserveResponseShapesAndExplicitTerminalNulls()
    {
        using var file = JsonDocument.Parse(WireJson.WriteTextFileReadResponse("contents"));
        Assert.Equal("contents", file.RootElement.GetProperty("content").GetString());

        using var output = JsonDocument.Parse(WireJson.WriteTerminalOutputResponse(new TerminalOutputResponse { Output = "stdout", Truncated = false }));
        Assert.False(output.RootElement.TryGetProperty("exitStatus", out _));

        using var exit = JsonDocument.Parse(WireJson.WriteTerminalExitStatusResponse(new TerminalExitStatus()));
        Assert.Equal(JsonValueKind.Null, exit.RootElement.GetProperty("exitCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, exit.RootElement.GetProperty("signal").ValueKind);

        using var accepted = JsonDocument.Parse(WireJson.WriteElicitationResponse(new ElicitationResponse(
            "accept", JsonDocument.Parse("{\"answer\":\"yes\"}").RootElement.Clone())));
        Assert.Equal("accept", accepted.RootElement.GetProperty("action").GetString());
        Assert.Equal("yes", accepted.RootElement.GetProperty("content").GetProperty("answer").GetString());

        using var declined = JsonDocument.Parse(WireJson.WriteElicitationResponse(new ElicitationResponse("decline")));
        Assert.Equal("decline", declined.RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public void RemainingRequestWritersUseTheExpectedSchemaShapes()
    {
        using var list = JsonDocument.Parse(WireJson.WriteListSessionsRequest("C:/workspace", "cursor-1"));
        Assert.Equal("C:/workspace", list.RootElement.GetProperty("cwd").GetString());
        Assert.Equal("cursor-1", list.RootElement.GetProperty("cursor").GetString());

        using var resume = JsonDocument.Parse(WireJson.WriteResumeSessionRequest("session-2", new SessionOptions
        {
            WorkingDirectory = "C:/workspace",
            AdditionalDirectories = ["C:/shared"],
            McpServers = []
        }));
        Assert.Equal("session-2", resume.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("C:/shared", resume.RootElement.GetProperty("additionalDirectories")[0].GetString());
        Assert.False(resume.RootElement.TryGetProperty("mcpServers", out _));

        using var boolean = JsonDocument.Parse(WireJson.WriteSetConfigOptionRequest("session-2", "confirm", new ConfigValue(null, true)));
        Assert.Equal("session-2", boolean.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("confirm", boolean.RootElement.GetProperty("configId").GetString());
        Assert.Equal("boolean", boolean.RootElement.GetProperty("type").GetString());
        Assert.True(boolean.RootElement.GetProperty("value").GetBoolean());

        using var choice = JsonDocument.Parse(WireJson.WriteSetConfigOptionRequest("session-2", "model", new ConfigValue("fast", null)));
        Assert.Equal("fast", choice.RootElement.GetProperty("value").GetString());
        Assert.False(choice.RootElement.TryGetProperty("type", out _));

        using var permission = JsonDocument.Parse(WireJson.WritePermissionResponse(PermissionDecision.Select("allow-once")));
        Assert.Equal("selected", permission.RootElement.GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.Equal("allow-once", permission.RootElement.GetProperty("outcome").GetProperty("optionId").GetString());
    }

    [Fact]
    public void SessionReadersRetainInitialStateAndAdditionalDirectories()
    {
        using var response = JsonDocument.Parse("""
            {
              "modes": { "currentModeId": "code", "availableModes": [{ "id": "code", "name": "Code", "description": "Edits files" }] },
              "configOptions": [{ "id": "model", "type": "select", "name": "Model", "currentValue": "fast", "options": [{ "value": "fast", "name": "Fast" }] }]
            }
            """);
        var session = new Session(null!, "session-1");

        WireJson.ApplySessionInitialState(session, response.RootElement);

        Assert.Equal("code", session.ModeState?.CurrentModeId);
        Assert.Equal("Code", Assert.Single(session.ModeState!.AvailableModes).Name);
        Assert.Equal("fast", Assert.IsType<SessionConfigOptionVariant0Select>(Assert.Single(session.ConfigOptions)).CurrentValue);

        using var sessions = JsonDocument.Parse("""
            { "sessions": [{ "sessionId": "session-1", "cwd": "C:/workspace", "additionalDirectories": ["C:/shared"], "title": "Work" }] }
            """);
        var listed = Assert.Single(WireJson.ReadSessionListPage(sessions.RootElement).Sessions);
        Assert.Equal("session-1", listed.SessionId);
        Assert.Equal("C:/workspace", listed.Cwd);
        Assert.Equal("C:/shared", Assert.Single(listed.AdditionalDirectories!));
    }

    [Fact]
    public void ToolCallReaderPreservesUnknownContentAsRawJson()
    {
        using var notification = JsonDocument.Parse("""
            { "update": { "sessionUpdate": "tool_call", "toolCallId": "tool-1", "content": [{ "type": "future_variant", "payload": { "value": 42 } }] } }
            """);

        var update = Assert.IsType<AgentToolCallUpdate>(WireJson.ReadSessionUpdate(notification.RootElement));
        var content = Assert.IsType<UnknownContent>(Assert.Single(update.Content!));

        Assert.Equal("future_variant", content.Type);
        Assert.Equal(42, content.RawPayload.GetProperty("payload").GetProperty("value").GetInt32());
    }

    [Fact]
    public void SessionEventsReuseGeneratedPlanAndToolLocationModels()
    {
        using var planNotification = JsonDocument.Parse("""
            { "update": { "sessionUpdate": "plan", "entries": [{ "content": "Inspect", "priority": "high", "status": "in_progress" }] } }
            """);
        var plan = Assert.IsType<PlanUpdate>(WireJson.ReadSessionUpdate(planNotification.RootElement));
        Assert.Equal("Inspect", Assert.IsType<PlanEntry>(Assert.Single(plan.Entries)).Content);

        using var toolNotification = JsonDocument.Parse("""
            { "update": { "sessionUpdate": "tool_call", "toolCallId": "tool-1", "title": "Inspect file", "locations": [{ "path": "src/a.cs", "line": 12 }] } }
            """);
        var tool = Assert.IsType<AgentToolCallUpdate>(WireJson.ReadSessionUpdate(toolNotification.RootElement));
        var location = Assert.IsType<ToolCallLocation>(Assert.Single(tool.Locations!));
        Assert.Equal("src/a.cs", location.Path);
        Assert.Equal(12u, location.Line);
    }

    [Fact]
    public void AgentImplementationTitleIsRetained()
    {
        using var response = JsonDocument.Parse("""
            { "protocolVersion": 1, "agentInfo": { "name": "agent", "version": "1", "title": "Agent title" } }
            """);

        Assert.Equal("Agent title", WireJson.ReadAgentInfo(response.RootElement).Title);
    }

    private static void AssertGeneratedUnionRoundTrip<T>(string expectedJson, byte[] serialized, Func<T, byte[]> serialize, Func<JsonElement, T> deserialize)
    {
        AssertJsonEqual(expectedJson, serialized);
        using var document = JsonDocument.Parse(serialized);
        AssertJsonEqual(expectedJson, serialize(deserialize(document.RootElement)));
    }

    private static void AssertUnionRoundTrip<T>(string json, Func<JsonElement, T> deserialize, Func<T, byte[]> serialize)
    {
        using var document = JsonDocument.Parse(json);
        AssertJsonEqual(json, serialize(deserialize(document.RootElement)));
    }

    private static void AssertMcpServer(string json, Type expectedType)
    {
        using var document = JsonDocument.Parse(json);
        var server = GeneratedJson.DeserializeMcpServer(document.RootElement);
        Assert.IsType(expectedType, server);
        AssertJsonEqual(json, GeneratedJson.Serialize(server));
    }

    private static void AssertJsonEqual(string expected, byte[] actual)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        Assert.True(
            JsonElement.DeepEquals(expectedDocument.RootElement, actualDocument.RootElement),
            $"Expected JSON {expectedDocument.RootElement.GetRawText()} but got {actualDocument.RootElement.GetRawText()}.");
    }
}
