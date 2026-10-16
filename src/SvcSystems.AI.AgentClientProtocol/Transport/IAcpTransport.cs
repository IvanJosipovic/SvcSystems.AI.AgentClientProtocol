namespace SvcSystems.AI.AgentClientProtocol.Transport;

internal interface IAcpTransport : IAsyncDisposable
{
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);

    IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken);
}
