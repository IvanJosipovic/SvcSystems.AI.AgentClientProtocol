namespace SvcSystems.AI.AgentClientProtocol.SampleAgent;

/// <summary>Mutable per-session state owned by <see cref="SampleAgentHandler"/>.</summary>
internal sealed class SampleSession
{
    private readonly object _gate = new();
    private CancellationTokenSource? _activePrompt;

    public SampleSession(string sessionId, string cwd, IReadOnlyList<string>? additionalDirectories)
    {
        SessionId = sessionId;
        Cwd = cwd;
        AdditionalDirectories = additionalDirectories is { Count: > 0 } ? [.. additionalDirectories] : null;
    }

    public string SessionId { get; }

    public string Cwd { get; }

    public List<string>? AdditionalDirectories { get; }

    public string CurrentModeId { get; set; } = "default";

    public string Verbosity { get; set; } = "normal";

    public bool ConfirmActions { get; set; }

    /// <summary>Registers the cancellation source that resolves this session's in-flight prompt.</summary>
    public void BeginPrompt(CancellationTokenSource prompt)
    {
        lock (_gate)
        {
            _activePrompt = prompt;
        }
    }

    /// <summary>Clears <paramref name="prompt"/> if it is still the registered one.</summary>
    public void EndPrompt(CancellationTokenSource prompt)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activePrompt, prompt))
            {
                _activePrompt = null;
            }
        }
    }

    /// <summary>
    /// Cancels an in-flight prompt so a `session/close` resolves it as `cancelled`, matching the
    /// `session/cancel` path the host already wires up.
    /// </summary>
    public void CancelActivePrompt()
    {
        CancellationTokenSource? prompt;
        lock (_gate)
        {
            prompt = _activePrompt;
        }

        try
        {
            prompt?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The prompt completed and disposed its source between the snapshot and the cancel.
        }
    }
}
