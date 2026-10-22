namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Runs an agent authentication flow that requires an interactive terminal.</summary>
public interface ITerminalAuthenticationHandler
{
    /// <summary>Runs the configured agent invocation with the authentication-specific arguments and environment.</summary>
    ValueTask<int> RunAsync(TerminalAuthenticationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Terminal invocation required by an ACP terminal authentication method.</summary>
/// <param name="FileName">The host-configured executable to run.</param>
/// <param name="Arguments">The host-configured arguments followed by any agent-supplied arguments.</param>
/// <param name="WorkingDirectory">The working directory for the invocation.</param>
/// <param name="Environment">The host-configured environment.</param>
/// <param name="AgentArguments">Arguments the agent asked to append, as advertised by the agent.</param>
/// <param name="AgentEnvironment">Environment variables the agent asked to set, as advertised by the agent.</param>
/// <remarks>
/// The agent is an untrusted peer. <see cref="AgentEnvironment"/> is supplied separately from
/// <see cref="Environment"/> so that a handler can tell which values came from the agent and
/// decide whether to apply them. The agent cannot overwrite a host variable unless the handler
/// deliberately merges <see cref="AgentEnvironment"/> into the environment it launches.
/// </remarks>
public sealed record TerminalAuthenticationRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    IReadOnlyList<string> AgentArguments,
    IReadOnlyDictionary<string, string> AgentEnvironment);
