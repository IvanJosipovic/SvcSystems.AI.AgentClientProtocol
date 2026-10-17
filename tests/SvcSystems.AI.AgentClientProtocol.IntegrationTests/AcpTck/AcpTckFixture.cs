using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.IntegrationTests;

internal static class AcpTckFixture
{
    public static ProcessOptions Process(
        string fixture,
        Action<string>? stderr = null,
        int protocolVersion = 1,
        int maximumMessageBytes = 8 * 1024 * 1024)
    {
        var root = Environment.GetEnvironmentVariable("ACP_TCK_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            Assert.Skip("Set ACP_TCK_ROOT and ACP_TCK_PYTHON, or run tools\\Test-AcpTck.ps1.");
        }

        var python = Environment.GetEnvironmentVariable("ACP_TCK_PYTHON");
        if (string.IsNullOrWhiteSpace(python))
        {
            throw new InvalidOperationException("ACP_TCK_ROOT is configured but ACP_TCK_PYTHON is missing.");
        }

        var script = Path.Combine(root, "tests", "fixtures", "agents", $"v{protocolVersion}", $"{fixture}.py");
        if (!File.Exists(script))
        {
            throw new FileNotFoundException("The configured ACP TCK checkout does not contain the fixture.", script);
        }

        return new ProcessOptions
        {
            FileName = python,
            Arguments = ["-B", "-X", "utf8", script],
            WorkingDirectory = Path.GetTempPath(),
            Environment = new Dictionary<string, string?>
            {
                ["ACP_TCK_CLIENT_PROBE"] = "stdio-environment",
            },
            StandardErrorReceived = stderr,
            ShutdownTimeout = TimeSpan.FromSeconds(1),
            MaximumMessageBytes = maximumMessageBytes,
        };
    }
}
