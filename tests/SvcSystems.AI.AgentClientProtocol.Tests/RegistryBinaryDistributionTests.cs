using System.Text.Json;
using SvcSystems.AI.AgentClientProtocol.Registry.Serialization;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

public sealed class RegistryBinaryDistributionTests
{
    [Fact]
    public void RegistryBinaryTargetsAreRetainedAsPlatformKeyedJson()
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

        var targetJson = Assert.Single(agent.Distribution.Binary!.Targets!).Value;
        var target = targetJson.Deserialize(RegistryJsonContext.Default.binaryTarget)!;
        Assert.Equal("https://example.test/agent.zip", target.Archive);
        Assert.Equal("agent.exe", target.Cmd);
        Assert.Equal(new[] { "acp" }, target.Args);
        Assert.Equal("safe", target.Env!["AGENT_MODE"]);
    }
}
