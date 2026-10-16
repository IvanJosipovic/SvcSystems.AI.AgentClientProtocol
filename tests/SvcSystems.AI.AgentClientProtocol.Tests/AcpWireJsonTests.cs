using System.Text.Json;
using SvcSystems.AI.AgentClientProtocol;
using SvcSystems.AI.AgentClientProtocol.Protocol.Generated;
using SvcSystems.AI.AgentClientProtocol.Serialization;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

public sealed class AcpWireJsonTests
{
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
        var options = new AcpClientOptions
        {
            ClientInfo = new AcpImplementationInfo("wire-test", "1.2.3"),
            SupportsCompactionUpdates = false,
            SupportsNoticeUpdates = false,
            SupportsBooleanSessionConfigOptions = false
        };

        using var document = JsonDocument.Parse(AcpWireJson.WriteInitializeRequest(request, options.ClientInfo, options));
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
        using var document = JsonDocument.Parse(AcpWireJson.WritePromptRequest(
            "session-1",
            [
                new AcpTextContent("hello"),
                new AcpResourceLink("source", "file:///workspace/source.txt", "desc", "text/plain")
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
        using var document = JsonDocument.Parse(AcpWireJson.WritePromptRequest("s", [
            new AcpTextContent("text"),
            new AcpImageContent("image-data", "image/png"),
            new AcpAudioContent("audio-data", "audio/wav"),
            new AcpEmbeddedResource("file:///text", "body", null, "text/plain"),
            new AcpEmbeddedResource("file:///blob", null, "base64", "application/octet-stream")
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
        var servers = new AcpMcpServerOptions[]
        {
            new AcpStdioMcpServerOptions("stdio", "C:/tools/server.exe", ["--quiet"], new Dictionary<string, string> { ["MODE"] = "test" }),
            new AcpHttpMcpServerOptions("http", "https://example.test/mcp", new Dictionary<string, string> { ["Authorization"] = "Bearer token" }),
            new AcpSseMcpServerOptions("sse", "https://example.test/events")
        };

        using var document = JsonDocument.Parse(AcpWireJson.WriteNewSessionRequest(request, servers));
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
        using var file = JsonDocument.Parse(AcpWireJson.WriteTextFileReadResponse("contents"));
        Assert.Equal("contents", file.RootElement.GetProperty("content").GetString());

        using var output = JsonDocument.Parse(AcpWireJson.WriteTerminalOutputResponse(new AcpTerminalOutput("stdout", false, null)));
        Assert.False(output.RootElement.TryGetProperty("exitStatus", out _));

        using var exit = JsonDocument.Parse(AcpWireJson.WriteTerminalExitStatusResponse(new AcpTerminalExitStatus(null)));
        Assert.Equal(JsonValueKind.Null, exit.RootElement.GetProperty("exitCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, exit.RootElement.GetProperty("signal").ValueKind);

        using var accepted = JsonDocument.Parse(AcpWireJson.WriteElicitationResponse(new AcpElicitationResponse(
            "accept", JsonDocument.Parse("{\"answer\":\"yes\"}").RootElement.Clone())));
        Assert.Equal("accept", accepted.RootElement.GetProperty("action").GetString());
        Assert.Equal("yes", accepted.RootElement.GetProperty("content").GetProperty("answer").GetString());

        using var declined = JsonDocument.Parse(AcpWireJson.WriteElicitationResponse(new AcpElicitationResponse("decline")));
        Assert.Equal("decline", declined.RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public void RemainingRequestWritersUseTheExpectedSchemaShapes()
    {
        using var list = JsonDocument.Parse(AcpWireJson.WriteListSessionsRequest("C:/workspace", "cursor-1"));
        Assert.Equal("C:/workspace", list.RootElement.GetProperty("cwd").GetString());
        Assert.Equal("cursor-1", list.RootElement.GetProperty("cursor").GetString());

        using var resume = JsonDocument.Parse(AcpWireJson.WriteResumeSessionRequest("session-2", new AcpSessionOptions
        {
            WorkingDirectory = "C:/workspace",
            AdditionalDirectories = ["C:/shared"],
            McpServers = []
        }));
        Assert.Equal("session-2", resume.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("C:/shared", resume.RootElement.GetProperty("additionalDirectories")[0].GetString());
        Assert.False(resume.RootElement.TryGetProperty("mcpServers", out _));

        using var boolean = JsonDocument.Parse(AcpWireJson.WriteSetConfigOptionRequest("session-2", "confirm", new AcpConfigValue(null, true)));
        Assert.Equal("session-2", boolean.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("confirm", boolean.RootElement.GetProperty("configId").GetString());
        Assert.Equal("boolean", boolean.RootElement.GetProperty("type").GetString());
        Assert.True(boolean.RootElement.GetProperty("value").GetBoolean());

        using var choice = JsonDocument.Parse(AcpWireJson.WriteSetConfigOptionRequest("session-2", "model", new AcpConfigValue("fast", null)));
        Assert.Equal("fast", choice.RootElement.GetProperty("value").GetString());
        Assert.False(choice.RootElement.TryGetProperty("type", out _));

        using var permission = JsonDocument.Parse(AcpWireJson.WritePermissionResponse(AcpPermissionDecision.Select("allow-once")));
        Assert.Equal("selected", permission.RootElement.GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.Equal("allow-once", permission.RootElement.GetProperty("outcome").GetProperty("optionId").GetString());
    }
}
