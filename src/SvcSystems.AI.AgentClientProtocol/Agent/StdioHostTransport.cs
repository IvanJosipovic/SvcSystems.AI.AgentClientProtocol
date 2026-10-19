using SvcSystems.AI.AgentClientProtocol.Transport;

namespace SvcSystems.AI.AgentClientProtocol.Agent;

internal sealed class StdioHostTransport(int maximumMessageBytes) : ITransport
{
    private readonly Stream _input = Console.OpenStandardInput();
    private readonly Stream _output = Console.OpenStandardOutput();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _disposed;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            await _output.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in StdioTransport.ReadFramesAsync(_input, maximumMessageBytes, cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }
}
