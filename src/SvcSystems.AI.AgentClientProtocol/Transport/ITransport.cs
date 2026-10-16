namespace SvcSystems.AI.AgentClientProtocol.Transport;

internal interface ITransport : IAsyncDisposable
{
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);

    IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken);
}
