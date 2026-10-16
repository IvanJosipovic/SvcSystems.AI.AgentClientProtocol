namespace SvcSystems.AI.AgentClientProtocol;

using SvcSystems.AI.AgentClientProtocol.Generated;

/// <summary>Host-provided filesystem operations that an ACP agent may request.</summary>
public interface IFileSystemHandler
{
    /// <summary>Reads text from an absolute path in the host's permitted workspace.</summary>
    ValueTask<string> ReadTextFileAsync(string sessionId, string path, uint? line, uint? limit, CancellationToken cancellationToken = default);

    /// <summary>Writes text to an absolute path in the host's permitted workspace.</summary>
    ValueTask WriteTextFileAsync(string sessionId, string path, string content, CancellationToken cancellationToken = default);
}
