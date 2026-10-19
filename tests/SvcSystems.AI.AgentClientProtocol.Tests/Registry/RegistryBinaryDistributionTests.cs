using System.Text.Json;
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
        Assert.Equal("agent.exe", restored.Distribution.Binary!.Targets["windows-x86_64"].Cmd);
    }
}
