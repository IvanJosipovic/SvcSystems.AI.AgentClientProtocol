namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Runs an agent authentication flow that requires an interactive terminal.</summary>
public interface IAcpTerminalAuthenticationHandler
{
    /// <summary>Runs the configured agent invocation with the authentication-specific arguments and environment.</summary>
    ValueTask<int> RunAsync(AcpTerminalAuthenticationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Terminal invocation required by an ACP terminal authentication method.</summary>
public sealed record AcpTerminalAuthenticationRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment);
