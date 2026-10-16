using SvcSystems.AI.AgentClientProtocol;

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("Usage: SvcSystems.AI.AgentClientProtocol.Console [agent-executable] [agent-arguments...]");
    Console.WriteLine("ACP_PROMPT can set the default prompt text.");
    return;
}

var executable = args.FirstOrDefault() ?? "opencode";
var agentArguments = args.Length > 1 ? args.Skip(1).ToArray() : ["acp"];
var prompt = Environment.GetEnvironmentVariable("ACP_PROMPT") ??
    "Explain the repository in a short, actionable summary.";

await using var client = await Client.StartAsync(
    new ProcessOptions
    {
        FileName = executable,
        Arguments = agentArguments,
        WorkingDirectory = Directory.GetCurrentDirectory(),
        StandardErrorReceived = line => Console.Error.WriteLine("[agent] " + line)
    });

Console.WriteLine($"Connected to {client.Agent.Name} {client.Agent.Version} (ACP v{client.Agent.ProtocolVersion}).");
var session = await client.CreateSessionAsync(new SessionOptions
{
    WorkingDirectory = Directory.GetCurrentDirectory()
});

await foreach (var update in session.PromptAsync(prompt))
{
    Console.WriteLine(update);
}
