namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>ACP request to read text from a host file.</summary>
public sealed record AcpTextFileReadRequest(string SessionId, string Path, uint? Line, uint? Limit);

/// <summary>ACP request to write text to a host file.</summary>
public sealed record AcpTextFileWriteRequest(string SessionId, string Path, string Content);

/// <summary>Host-provided filesystem operations that an ACP agent may request.</summary>
public interface IAcpFileSystemHandler
{
    /// <summary>Reads text from an absolute path in the host's permitted workspace.</summary>
    ValueTask<string> ReadTextFileAsync(string sessionId, string path, uint? line, uint? limit, CancellationToken cancellationToken = default);

    /// <summary>Writes text to an absolute path in the host's permitted workspace.</summary>
    ValueTask WriteTextFileAsync(string sessionId, string path, string content, CancellationToken cancellationToken = default);
}
