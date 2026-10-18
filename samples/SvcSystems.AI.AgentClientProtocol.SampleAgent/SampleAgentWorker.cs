using AcpAgent = SvcSystems.AI.AgentClientProtocol.Agent.Agent;

namespace SvcSystems.AI.AgentClientProtocol.SampleAgent;

/// <summary>
/// Bridges the generic host to the agent host: the hosted service runs the ACP agent over
/// stdin/stdout and stops the host as soon as the client disconnects, so the process exits
/// promptly once its input ends.
/// </summary>
internal sealed class SampleAgentWorker : BackgroundService
{
    private readonly AcpAgent _agent;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SampleAgentWorker> _logger;

    public SampleAgentWorker(AcpAgent agent, IHostApplicationLifetime lifetime, ILogger<SampleAgentWorker> logger)
    {
        _agent = agent;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _agent.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (AgentException exception)
        {
            // An unusable or malformed client frame faults the connection. Report it once on
            // standard error and exit non-zero rather than letting the process die with an
            // unhandled exception dump.
            _logger.LogError(exception, "ACP connection failed: {Message}", exception.Message);
            Environment.ExitCode = 1;
        }
        finally
        {
            // The agent run ends when stdin closes or the connection faults. There is no more
            // work, so stop the host instead of waiting for a shutdown signal.
            _lifetime.StopApplication();
        }
    }
}
