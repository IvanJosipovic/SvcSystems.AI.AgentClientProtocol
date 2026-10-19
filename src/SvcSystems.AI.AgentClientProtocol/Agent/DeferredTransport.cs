using SvcSystems.AI.AgentClientProtocol.Transport;

namespace SvcSystems.AI.AgentClientProtocol.Agent;

internal sealed class DeferredTransport(ITransport inner) : ITransport
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Start() => _started.TrySetResult();

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) =>
        inner.SendAsync(message, cancellationToken);

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await _started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var message in inner.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    public ValueTask DisposeAsync()
    {
        Start();
        return inner.DisposeAsync();
    }
}
