namespace SvcSystems.AI.AgentClientProtocol;

using SvcSystems.AI.AgentClientProtocol.Generated;

/// <summary>Host-provided terminal operations. Configure an implementation only when agent command execution is allowed.</summary>
public interface ITerminalHandler
{
    /// <summary>Creates a terminal and starts the requested command.</summary>
    ValueTask<string> CreateAsync(CreateTerminalRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads output captured so far and reports the current exit status, if complete.</summary>
    ValueTask<TerminalOutputResponse> GetOutputAsync(string sessionId, string terminalId, CancellationToken cancellationToken = default);

    /// <summary>Waits for command completion and returns its exit status.</summary>
    ValueTask<TerminalExitStatus> WaitForExitAsync(string sessionId, string terminalId, CancellationToken cancellationToken = default);

    /// <summary>Kills a terminal command but keeps the terminal available for output retrieval.</summary>
    ValueTask KillAsync(string sessionId, string terminalId, CancellationToken cancellationToken = default);

    /// <summary>Releases terminal resources and kills a command that is still running.</summary>
    ValueTask ReleaseAsync(string sessionId, string terminalId, CancellationToken cancellationToken = default);
}
