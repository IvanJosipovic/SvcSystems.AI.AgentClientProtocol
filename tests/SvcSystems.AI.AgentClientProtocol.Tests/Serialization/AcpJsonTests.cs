using System.Text.Json;
using SvcSystems.AI.AgentClientProtocol.Serialization;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

public sealed class AcpJsonTests
{
    [Fact]
    public void SerializesAndDeserializesModelsWithTheGeneratedContract()
    {
        var request = new ReadTextFileRequest { SessionId = "session-1", Path = "notes.txt", Line = 3 };

        using var document = JsonDocument.Parse(AcpJson.SerializeToUtf8Bytes(request));
        Assert.Equal("session-1", document.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("notes.txt", document.RootElement.GetProperty("path").GetString());
        Assert.False(document.RootElement.TryGetProperty("limit", out _));

        var fromElement = AcpJson.Deserialize<ReadTextFileRequest>(AcpJson.SerializeToElement(request));
        Assert.NotNull(fromElement);
        Assert.Equal("notes.txt", fromElement.Path);
        Assert.Equal(3u, fromElement.Line);

        var fromBytes = AcpJson.Deserialize<ReadTextFileRequest>(JsonSerializer.SerializeToUtf8Bytes(request, AcpJson.Options));
        Assert.NotNull(fromBytes);
        Assert.Equal("session-1", fromBytes.SessionId);
    }

    [Fact]
    public void ExposesTheGeneratedTypeMetadata()
    {
        var typeInfo = AcpJson.TypeInfo<ReadTextFileRequest>();
        Assert.Equal(typeof(ReadTextFileRequest), typeInfo.Type);
        Assert.Same(typeInfo, AcpJson.TypeInfo<ReadTextFileRequest>());

        // Union models carry generated discriminators, so they round trip through the same contract.
        var outcome = AcpJson.Deserialize<RequestPermissionResponse>(
            JsonDocument.Parse("""{"outcome":{"outcome":"selected","optionId":"allow"}}""").RootElement);
        Assert.NotNull(outcome);
        Assert.Equal("allow", Assert.IsType<SelectedPermissionOutcome>(outcome.Outcome).OptionId);
    }

    [Fact]
    public void DefaultsAnnotatedRequiredCollectionsWhenJsonIsExplicitlyNull()
    {
        var request = AcpJson.Deserialize<NewSessionRequest>(
            JsonDocument.Parse("""{"cwd":"C:/workspace","mcpServers":null}""").RootElement);

        Assert.NotNull(request);
        Assert.Empty(request.McpServers);
    }

    [Fact]
    public void DefaultsAnnotatedOptionalCollectionsOnlyWhenNullIsProvided()
    {
        var omitted = AcpJson.Deserialize<CreateTerminalRequest>(
            JsonDocument.Parse("""{"sessionId":"session-1","command":"sh"}""").RootElement);
        var explicitNull = AcpJson.Deserialize<CreateTerminalRequest>(
            JsonDocument.Parse("""{"sessionId":"session-1","command":"sh","args":null}""").RootElement);
        var providedValue = AcpJson.Deserialize<CreateTerminalRequest>(
            JsonDocument.Parse("""{"sessionId":"session-1","command":"sh","args":["-c"]}""").RootElement);
        var assignedNull = new CreateTerminalRequest { SessionId = "session-1", Command = "sh", Args = null };

        Assert.NotNull(omitted);
        Assert.Null(omitted.Args);
        Assert.NotNull(explicitNull);
        Assert.Empty(explicitNull.Args!);
        Assert.NotNull(providedValue);
        Assert.Collection(providedValue.Args!, argument => Assert.Equal("-c", argument));
        Assert.Empty(assignedNull.Args!);
    }
}
