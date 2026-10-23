namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Host-provided terminal operations. Configure an implementation only when agent command execution is allowed.</summary>
/// <remarks>
/// The client validates only that a terminal's working directory is an absolute path. It does not
/// restrict the command, its arguments, or the working directory to the session's workspace:
/// terminal execution is delegated to this handler, which must enforce its own authorization,
/// workspace, and command policy.
/// </remarks>
public interface ITerminalHandler
{
    /// <summary>Creates a terminal and starts the requested command.</summary>
    ValueTask<string> CreateAsync(CreateTerminalRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads output captured so far and reports the current exit status, if complete.</summary>
    ValueTask<TerminalOutputResponse> GetOutputAsync(TerminalOutputRequest request, CancellationToken cancellationToken = default);

    /// <summary>Waits for command completion and returns its exit status.</summary>
    ValueTask<TerminalExitStatus> WaitForExitAsync(WaitForTerminalExitRequest request, CancellationToken cancellationToken = default);

    /// <summary>Kills a terminal command but keeps the terminal available for output retrieval.</summary>
    ValueTask KillAsync(KillTerminalRequest request, CancellationToken cancellationToken = default);

    /// <summary>Releases terminal resources and kills a command that is still running.</summary>
    ValueTask ReleaseAsync(ReleaseTerminalRequest request, CancellationToken cancellationToken = default);
}
