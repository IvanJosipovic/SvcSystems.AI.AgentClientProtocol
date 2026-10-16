using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>An ACP conversation session.</summary>
public sealed class AcpSession
{
    private static readonly TimeSpan CancellationDrainTimeout = TimeSpan.FromSeconds(15);
    private readonly AcpClient _client;
    private readonly Channel<AcpSessionUpdate> _updates = Channel.CreateUnbounded<AcpSessionUpdate>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly SemaphoreSlim _promptLock = new(1, 1);

    internal AcpSession(AcpClient client, string id)
    {
        _client = client;
        Id = id;
    }

    /// <summary>Agent-provided session id.</summary>
    public string Id { get; }

    /// <summary>Sends a text prompt and streams normalized ACP updates until the turn finishes.</summary>
    public IAsyncEnumerable<AcpSessionUpdate> PromptAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return PromptAsync([new AcpTextContent(prompt)], cancellationToken);
    }

    /// <summary>Sends typed prompt content and streams normalized ACP updates until the turn finishes.</summary>
    public async IAsyncEnumerable<AcpSessionUpdate> PromptAsync(
        IReadOnlyList<AcpContentBlock> content,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        await _promptLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task<string>? promptTask = null;
        using var promptRequestCancellation = new CancellationTokenSource();
        var completed = false;
        try
        {
            promptTask = _client.PromptAsync(Id, content, promptRequestCancellation.Token);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var updateTask = _updates.Reader.WaitToReadAsync(cancellationToken).AsTask();
                var finished = await Task.WhenAny(promptTask, updateTask).ConfigureAwait(false);

                if (finished == updateTask)
                {
                    if (!await updateTask.ConfigureAwait(false)) break;
                    while (_updates.Reader.TryRead(out var update)) yield return update;
                    continue;
                }

                var stopReason = await promptTask.ConfigureAwait(false);
                while (_updates.Reader.TryRead(out var update)) yield return update;
                completed = true;
                yield return new AcpCompleted(stopReason);
                break;
            }
        }
        finally
        {
            if (!completed && promptTask is { IsCompleted: false })
            {
                try { await _client.CancelSessionAsync(Id, CancellationToken.None).ConfigureAwait(false); }
                catch { /* the connection may already be closing */ }

                var drained = await Task.WhenAny(promptTask, Task.Delay(CancellationDrainTimeout)).ConfigureAwait(false);
                if (drained != promptTask)
                {
                    promptRequestCancellation.Cancel();
                }
                else
                {
                    try { await promptTask.ConfigureAwait(false); }
                    catch { /* preserve cancellation or consumer-disposal as the primary outcome */ }
                }
            }
            _promptLock.Release();
        }
    }

    /// <summary>Cancels the active prompt turn, if any.</summary>
    public Task CancelAsync(CancellationToken cancellationToken = default) =>
        _client.CancelSessionAsync(Id, cancellationToken);

    /// <summary>Changes the session's current operating mode.</summary>
    public Task SetModeAsync(string modeId, CancellationToken cancellationToken = default) =>
        _client.SetSessionModeAsync(Id, modeId, cancellationToken);

    /// <summary>Sets a string-valued session configuration option.</summary>
    public Task<IReadOnlyList<AcpConfigOption>> SetConfigOptionAsync(string configId, string valueId, CancellationToken cancellationToken = default) =>
        _client.SetSessionConfigOptionAsync(Id, configId, AcpConfigValue.FromValueId(valueId), cancellationToken);

    /// <summary>Sets a boolean-valued session configuration option.</summary>
    public Task<IReadOnlyList<AcpConfigOption>> SetConfigOptionAsync(string configId, bool value, CancellationToken cancellationToken = default) =>
        _client.SetSessionConfigOptionAsync(Id, configId, AcpConfigValue.FromBoolean(value), cancellationToken);

    internal void Publish(AcpSessionUpdate update) => _updates.Writer.TryWrite(update);
}
