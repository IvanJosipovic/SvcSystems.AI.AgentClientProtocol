using SvcSystems.AI.AgentClientProtocol.Agent;
using SvcSystems.AI.AgentClientProtocol.SampleAgent;

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("Usage: SvcSystems.AI.AgentClientProtocol.SampleAgent");
    Console.WriteLine("Serves a deterministic ACP agent over stdin/stdout for a connected client.");
    Console.WriteLine("tools/Test-AcpTck.ps1 drives it as an agent conformance target.");
    return;
}

// A conformance runner starts this binary from an arbitrary working directory, so anchor the
// content root to the output directory to keep appsettings.json discoverable.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Standard output is reserved for ACP frames, so every log record is routed to standard error.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<IAgentHandler, SampleAgentHandler>();

// The agent host is a singleton so the container owns its asynchronous disposal; the worker
// bridges it to the host lifetime.
builder.Services.AddSingleton(services => new Agent(services.GetRequiredService<IAgentHandler>()));
builder.Services.AddHostedService<SampleAgentWorker>();

await builder.Build().RunAsync();
