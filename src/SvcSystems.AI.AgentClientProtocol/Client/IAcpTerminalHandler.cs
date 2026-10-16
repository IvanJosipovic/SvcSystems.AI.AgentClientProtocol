namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Host-provided terminal operations. Configure an implementation only when agent command execution is allowed.</summary>
public interface IAcpTerminalHandler
{
    /// <summary>Creates a terminal and starts the requested command.</summary>
    ValueTask<string> CreateAsync(AcpTerminalCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads output captured so far and reports the current exit status, if complete.</summary>
    ValueTask<AcpTerminalOutput> GetOutputAsync(string sessionId, string terminalId, CancellationToken cancellationToken = default);

    /// <summary>Waits for command completion and returns its exit status.</summary>
    ValueTask<AcpTerminalExitStatus> WaitForExitAsync(string sessionId, string terminalId, CancellationToken cancellationToken = default);

    /// <summary>Kills a terminal command but keeps the terminal available for output retrieval.</summary>
    ValueTask KillAsync(string sessionId, string terminalId, CancellationToken cancellationToken = default);

    /// <summary>Releases terminal resources and kills a command that is still running.</summary>
    ValueTask ReleaseAsync(string sessionId, string terminalId, CancellationToken cancellationToken = default);
}

/// <summary>Agent request to launch a terminal command.</summary>
public sealed record AcpTerminalCreateRequest(
    string SessionId,
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string? WorkingDirectory,
    ulong? OutputByteLimit);

/// <summary>Captured terminal output.</summary>
public sealed record AcpTerminalOutput(string Output, bool Truncated, AcpTerminalExitStatus? ExitStatus);

/// <summary>Terminal process completion status.</summary>
public sealed record AcpTerminalExitStatus(uint? ExitCode, string? Signal = null);
