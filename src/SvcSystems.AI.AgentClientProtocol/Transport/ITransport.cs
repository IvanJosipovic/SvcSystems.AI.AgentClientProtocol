namespace SvcSystems.AI.AgentClientProtocol.Transport;

/// <summary>Bidirectional framed transport used by an ACP client.</summary>
/// <remarks>Each send is one complete JSON-RPC message; each received item must contain one complete message.</remarks>
public interface ITransport : IAsyncDisposable
{
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);

    IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken);
}
