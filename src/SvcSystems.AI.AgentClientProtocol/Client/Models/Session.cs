using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SvcSystems.AI.AgentClientProtocol.Generated;

namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>An ACP conversation session.</summary>
public sealed class Session
{
    private static readonly TimeSpan CancellationDrainTimeout = TimeSpan.FromSeconds(15);
    private readonly Client _client;
    private readonly Channel<SessionEvent> _updates;
    private readonly int _maximumBufferedUpdates;
    private Exception? _updateBufferFailure;
    private readonly SemaphoreSlim _promptLock = new(1, 1);

    internal Session(Client client, string id, int maximumBufferedUpdates = 1024)
    {
        if (maximumBufferedUpdates <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBufferedUpdates));
        _client = client;
        Id = id;
        _maximumBufferedUpdates = maximumBufferedUpdates;
        _updates = Channel.CreateBounded<SessionEvent>(new BoundedChannelOptions(maximumBufferedUpdates)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    /// <summary>Agent-provided session id.</summary>
    public string Id { get; }

    /// <summary>The agent's initial or most recently reported mode state.</summary>
    public SessionModeState? ModeState { get; private set; }

    /// <summary>The agent's initial or most recently reported configuration options.</summary>
    public IReadOnlyList<SessionConfigOption> ConfigOptions { get; private set; } = Array.Empty<SessionConfigOption>();

    /// <summary>Sends a text prompt and streams normalized ACP updates until the turn finishes.</summary>
    public IAsyncEnumerable<SessionEvent> PromptAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return PromptAsync([new PromptTextContent(prompt)], cancellationToken);
    }

    /// <summary>Sends typed prompt content and streams normalized ACP updates until the turn finishes.</summary>
    public async IAsyncEnumerable<SessionEvent> PromptAsync(
        IReadOnlyList<AgentContentBlock> content,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        await _promptLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task<string>? promptTask = null;
        using var promptRequestCancellation = new CancellationTokenSource();
        var completed = false;
        try
        {
            ThrowIfUpdateBufferFailed();
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
                ThrowIfUpdateBufferFailed();
                completed = true;
                yield return new Completed(stopReason);
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
    public async Task<IReadOnlyList<SessionConfigOption>> SetConfigOptionAsync(string configId, string valueId, CancellationToken cancellationToken = default)
    {
        var options = await _client.SetSessionConfigOptionAsync(Id, configId, ConfigValue.FromValueId(valueId), cancellationToken).ConfigureAwait(false);
        ConfigOptions = options;
        return options;
    }

    /// <summary>Sets a boolean-valued session configuration option.</summary>
    public async Task<IReadOnlyList<SessionConfigOption>> SetConfigOptionAsync(string configId, bool value, CancellationToken cancellationToken = default)
    {
        var options = await _client.SetSessionConfigOptionAsync(Id, configId, ConfigValue.FromBoolean(value), cancellationToken).ConfigureAwait(false);
        ConfigOptions = options;
        return options;
    }

    internal bool Publish(SessionEvent update)
    {
        if (Volatile.Read(ref _updateBufferFailure) is not null) return false;
        switch (update)
        {
            case ModeUpdate mode when ModeState is { } state:
                ModeState = new SessionModeState
                {
                    CurrentModeId = mode.CurrentModeId,
                    AvailableModes = state.AvailableModes,
                    Meta = state.Meta,
                    ExtensionData = state.ExtensionData
                };
                break;
            case ModeUpdate mode:
                ModeState = new SessionModeState { CurrentModeId = mode.CurrentModeId, AvailableModes = [] };
                break;
            case ConfigOptionsUpdate config:
                ConfigOptions = config.Options;
                break;
        }
        if (_updates.Writer.TryWrite(update)) return true;

        var failure = new ProtocolException(
            $"Session '{Id}' produced more than {_maximumBufferedUpdates} unread updates; its update stream is no longer usable.");
        if (Interlocked.CompareExchange(ref _updateBufferFailure, failure, null) is null)
        {
            _updates.Writer.TryComplete(failure);
        }
        return false;
    }

    private void ThrowIfUpdateBufferFailed()
    {
        if (Volatile.Read(ref _updateBufferFailure) is { } failure)
        {
            throw new ProtocolException($"Session '{Id}' update buffering failed.", failure);
        }
    }

    internal void SetInitialState(SessionModeState? modeState, IReadOnlyList<SessionConfigOption>? configOptions)
    {
        if (modeState is not null) ModeState = modeState;
        if (configOptions is not null) ConfigOptions = configOptions;
    }
}
