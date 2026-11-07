using System.Text.Json;
using SvcSystems.AI.AgentClientProtocol.Registry.Models;
using SvcSystems.AI.AgentClientProtocol.Registry.Serialization;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

public sealed class RegistryBinaryDistributionTests
{
    [Fact]
    public void RegistryBinaryTargetsDeserializeIntoTypedPlatformMap()
    {
        const string json = """
            {
              "id": "agent",
              "name": "Agent",
              "version": "1.2.3",
              "description": "ACP agent",
              "distribution": {
                "binary": {
                  "windows-x86_64": {
                    "archive": "https://example.test/agent.zip",
                    "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "cmd": "agent.exe",
                    "args": ["acp"],
                    "env": { "AGENT_MODE": "safe" }
                  }
                }
              }
            }
            """;

        var agent = JsonSerializer.Deserialize(json, RegistryJsonContext.Default.RegistryAgent)!;

        var target = Assert.Single(agent.Distribution.Binary!.Targets).Value;
        Assert.Equal("https://example.test/agent.zip", target.Archive);
        Assert.Equal("agent.exe", target.Cmd);
        Assert.Equal(new[] { "acp" }, target.Args);
        Assert.Equal("safe", target.Env!["AGENT_MODE"]);

        var roundTrip = JsonSerializer.Serialize(agent, RegistryJsonContext.Default.RegistryAgent);
        var restored = JsonSerializer.Deserialize(roundTrip, RegistryJsonContext.Default.RegistryAgent)!;
        Assert.Equal("agent.exe", restored.Distribution.Binary!.Targets[BinaryDistributionPropertyName.WindowsX8664].Cmd);
    }

    [Fact]
    public void RegistryBinaryTargetsRejectUnknownPlatformNames()
    {
        const string json = "{ \"unknown-platform\": { \"archive\": \"https://example.test/a.zip\", \"cmd\": \"agent\" } }";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            json,
            RegistryJsonContext.Default.binaryDistribution));
    }

    [Fact]
    public void RegistryBinaryTargetsAcceptSchemaAddedMuslKeysAndResolveThemByWireName()
    {
        const string json = "{ \"linux-x86_64-musl\": { \"archive\": \"https://example.test/a.tar.gz\", \"cmd\": \"agent\" } }";

        var distribution = JsonSerializer.Deserialize(json, RegistryJsonContext.Default.binaryDistribution)!;

        Assert.True(distribution.TryGetTarget("linux-x86_64-musl", out var target));
        Assert.Equal("agent", target!.Cmd);
        Assert.False(distribution.TryGetTarget("linux-x86_64", out _));
    }

    [Fact]
    public void RegistryBinaryTargetsRequireAtLeastOnePlatform()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "{}",
            RegistryJsonContext.Default.binaryDistribution));
    }
}
