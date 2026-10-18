using System.Collections.Concurrent;
using System.Text.Json;
using SvcSystems.AI.AgentClientProtocol.Agent;
using SvcSystems.AI.AgentClientProtocol.Agent.Models;
using SvcSystems.AI.AgentClientProtocol.Models;

namespace SvcSystems.AI.AgentClientProtocol.SampleAgent;

/// <summary>
/// A deterministic, fully offline ACP agent used to exercise the agent-side host
/// (<see cref="Agent.Agent"/>) end to end. It advertises every optional capability the generated
/// models expose -- session load/resume/list/delete/close, additional directories, prompt
/// content types, modes, config options, and the authentication surface -- and implements the
/// matching <see cref="IAgentHandler"/> callbacks, so a run covers far more than the
/// mandatory request methods.
/// </summary>
/// <remarks>
/// Prompt handling is deliberately trivial: echo the prompt's first text block back as one
/// streamed <c>agent_message_chunk</c> and finish the turn with <c>end_turn</c>. The exact
/// prompt text <see cref="HangPromptText"/> instead keeps the turn open until a
/// <c>session/cancel</c> notification or a <c>session/close</c> request arrives, which is how a
/// conformance run can exercise cancellation against a real agent.
/// </remarks>
internal sealed class SampleAgentHandler : IAgentHandler
{
    /// <summary>The only protocol version this agent implements.</summary>
    private const long SupportedProtocolVersion = 1;

    private const string HangPromptText = "__hang__";
    private const string AuthenticationMethodId = "tck";
    private const string DefaultModeId = "default";
    private const string PlanModeId = "plan";
    private const string VerbosityConfigId = "verbosity";
    private const string ConfirmConfigId = "confirm";

    private readonly ConcurrentDictionary<string, SampleSession> _sessions = new(StringComparer.Ordinal);
    private volatile bool _clientSupportsBooleanConfigOptions;

    public ValueTask<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
    {
        _clientSupportsBooleanConfigOptions =
            request.ClientCapabilities?.Session?.ConfigOptions?.Boolean is not null;

        var response = new InitializeResponse
        {
            // Only protocol version 1 is implemented, so the negotiated version is always 1 --
            // never echo a version this agent does not actually support.
            ProtocolVersion = SupportedProtocolVersion,
            AgentInfo = new Implementation
            {
                Name = "SvcSystems.AI.AgentClientProtocol.SampleAgent",
                Version = typeof(SampleAgentHandler).Assembly.GetName().Version?.ToString() ?? "1.0.0"
            },
            AgentCapabilities = new AgentCapabilities
            {
                LoadSession = true,
                PromptCapabilities = new PromptCapabilities
                {
                    Image = true,
                    Audio = true,
                    EmbeddedContext = true
                },
                SessionCapabilities = new SessionCapabilities
                {
                    List = new SessionListCapabilities(),
                    Delete = new SessionDeleteCapabilities(),
                    Resume = new SessionResumeCapabilities(),
                    Close = new SessionCloseCapabilities(),
                    AdditionalDirectories = new SessionAdditionalDirectoriesCapabilities()
                },
                Auth = new AgentAuthCapabilities { Logout = new LogoutCapabilities() }
            },
            AuthMethods =
            [
                new AuthMethod(new AuthMethodAgent { Id = AuthenticationMethodId, Name = "Test authentication" })
            ]
        };

        return ValueTask.FromResult(response);
    }

    public ValueTask<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Cwd))
        {
            throw new RpcException(-32602, "session/new requires a non-empty cwd.", "session/new");
        }

        var session = new SampleSession(Guid.NewGuid().ToString("N"), request.Cwd, request.AdditionalDirectories);
        _sessions[session.SessionId] = session;

        var response = new NewSessionResponse
        {
            SessionId = session.SessionId,
            Modes = CreateModeState(session.CurrentModeId),
            ConfigOptions = VisibleConfigOptions(session)
        };

        return ValueTask.FromResult(response);
    }

    public async ValueTask<PromptResponse> PromptAsync(
        PromptRequest request,
        IAgentContext context,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(request.SessionId, out var session))
        {
            throw new RpcException(-32602, $"Unknown sessionId '{request.SessionId}'.", "session/prompt");
        }

        if (request.Prompt.Count == 0)
        {
            throw new RpcException(-32602, "session/prompt requires a non-empty prompt array.", "session/prompt");
        }

        var text = FirstTextBlock(request.Prompt) ?? string.Empty;
        await context.SendSessionUpdateAsync(
            request.SessionId,
            new SessionUpdate(new SessionUpdateVariant1AgentMessageChunk
            {
                Content = new ContentBlock(new TextContent { Text = text })
            }),
            cancellationToken).ConfigureAwait(false);

        if (!string.Equals(text, HangPromptText, StringComparison.Ordinal))
        {
            return new PromptResponse { StopReason = "end_turn" };
        }

        // The host links `cancellationToken` to its own prompt-cancellation source, which covers
        // `session/cancel`. A `session/close` is a separate request the host cannot translate
        // into prompt cancellation, so the handler keeps its own source for that path. Either
        // way the turn ends as `cancelled`.
        using var promptCancellation = new CancellationTokenSource();
        session.BeginPrompt(promptCancellation);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, promptCancellation.Token);
        try
        {
            // Never completes on its own; only cancellation releases the turn.
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token).ConfigureAwait(false);
            return new PromptResponse { StopReason = "end_turn" };
        }
        catch (OperationCanceledException) when (promptCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new PromptResponse { StopReason = "cancelled" };
        }
        finally
        {
            session.EndPrompt(promptCancellation);
        }
    }

    public ValueTask<LoadSessionResponse> LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken)
    {
        var session = GetOrCreate(request.SessionId, request.Cwd, request.AdditionalDirectories);

        // No replay is emitted: this method has no context to send `session/update`
        // notifications. ACP-LOAD-002 only forbids history arriving *after* the response, so
        // responding without replay is conformant.
        return ValueTask.FromResult(new LoadSessionResponse
        {
            Modes = CreateModeState(session.CurrentModeId),
            ConfigOptions = VisibleConfigOptions(session)
        });
    }

    public ValueTask<ResumeSessionResponse> ResumeSessionAsync(ResumeSessionRequest request, CancellationToken cancellationToken)
    {
        var session = GetOrCreate(request.SessionId, request.Cwd, request.AdditionalDirectories);
        return ValueTask.FromResult(new ResumeSessionResponse
        {
            Modes = CreateModeState(session.CurrentModeId),
            ConfigOptions = VisibleConfigOptions(session)
        });
    }

    public ValueTask<ListSessionsResponse> ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken)
    {
        var sessions = new List<SessionInfo>();
        foreach (var session in _sessions.Values)
        {
            if (request.Cwd is not null && !string.Equals(session.Cwd, request.Cwd, StringComparison.Ordinal))
            {
                continue;
            }

            sessions.Add(new SessionInfo
            {
                SessionId = session.SessionId,
                Cwd = session.Cwd,
                AdditionalDirectories = session.AdditionalDirectories is { Count: > 0 } ? [.. session.AdditionalDirectories] : null
            });
        }

        return ValueTask.FromResult(new ListSessionsResponse { Sessions = sessions });
    }

    public ValueTask<DeleteSessionResponse> DeleteSessionAsync(DeleteSessionRequest request, CancellationToken cancellationToken)
    {
        // Deleting an unknown session succeeds silently.
        _sessions.TryRemove(request.SessionId, out _);
        return ValueTask.FromResult(new DeleteSessionResponse());
    }

    public ValueTask<CloseSessionResponse> CloseSessionAsync(CloseSessionRequest request, CancellationToken cancellationToken)
    {
        // Closing an unknown session succeeds silently, like delete.
        if (_sessions.TryRemove(request.SessionId, out var session))
        {
            session.CancelActivePrompt();
        }

        return ValueTask.FromResult(new CloseSessionResponse());
    }

    public ValueTask<SetSessionModeResponse> SetSessionModeAsync(SetSessionModeRequest request, CancellationToken cancellationToken)
    {
        var session = Require(request.SessionId, "session/set_mode");
        if (request.ModeId is not (DefaultModeId or PlanModeId))
        {
            throw new RpcException(-32602, $"Unknown modeId '{request.ModeId}'.", "session/set_mode");
        }

        session.CurrentModeId = request.ModeId;
        return ValueTask.FromResult(new SetSessionModeResponse());
    }

    public ValueTask<SetSessionConfigOptionResponse> SetSessionConfigOptionAsync(
        SetSessionConfigOptionRequest request,
        CancellationToken cancellationToken)
    {
        // The generated request union models only the `value`/`type` variant fields; `sessionId`
        // and `configId` live on the enclosing request object and arrive as extension data.
        var (sessionId, configId) = request.Value switch
        {
            SetSessionConfigOptionRequestCase0 boolean => (
                ReadExtensionString(boolean.ExtensionData, "sessionId"),
                ReadExtensionString(boolean.ExtensionData, "configId")),
            SetSessionConfigOptionRequestCase1 selection => (
                ReadExtensionString(selection.ExtensionData, "sessionId"),
                ReadExtensionString(selection.ExtensionData, "configId")),
            _ => (null, null)
        };

        if (sessionId is null || configId is null)
        {
            throw new RpcException(-32602, "session/set_config_option requires sessionId and configId.", "session/set_config_option");
        }

        var session = Require(sessionId, "session/set_config_option");
        switch (request.Value)
        {
            case SetSessionConfigOptionRequestCase1 selection when string.Equals(configId, VerbosityConfigId, StringComparison.Ordinal):
                if (selection.Value is not ("quiet" or "normal" or "verbose"))
                {
                    throw new RpcException(-32602, $"Unknown value '{selection.Value}' for '{configId}'.", "session/set_config_option");
                }

                session.Verbosity = selection.Value;
                break;
            case SetSessionConfigOptionRequestCase0 boolean when string.Equals(configId, ConfirmConfigId, StringComparison.Ordinal):
                session.ConfirmActions = boolean.Value;
                break;
            default:
                throw new RpcException(-32602, $"Unknown config option '{configId}'.", "session/set_config_option");
        }

        return ValueTask.FromResult(new SetSessionConfigOptionResponse { ConfigOptions = VisibleConfigOptions(session) });
    }

    public ValueTask<AuthenticateResponse> AuthenticateAsync(AuthenticateRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.MethodId, AuthenticationMethodId, StringComparison.Ordinal))
        {
            throw new RpcException(-32602, $"Unknown authentication method '{request.MethodId}'.", "authenticate");
        }

        return ValueTask.FromResult(new AuthenticateResponse());
    }

    public ValueTask<LogoutResponse> LogoutAsync(LogoutRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new LogoutResponse());

    private static SessionModeState CreateModeState(string currentModeId) => new()
    {
        CurrentModeId = currentModeId,
        AvailableModes =
        [
            new SessionMode { Id = DefaultModeId, Name = "Default" },
            new SessionMode { Id = PlanModeId, Name = "Plan" }
        ]
    };

    private static string? FirstTextBlock(IReadOnlyList<ContentBlock> prompt)
    {
        foreach (var block in prompt)
        {
            if (block.Value is TextContent text)
            {
                return text.Text;
            }
        }

        return null;
    }

    private static string? ReadExtensionString(IReadOnlyDictionary<string, JsonElement>? extensionData, string name) =>
        extensionData is not null && extensionData.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private SampleSession GetOrCreate(string sessionId, string cwd, IReadOnlyList<string>? additionalDirectories) =>
        _sessions.GetOrAdd(sessionId, _ => new SampleSession(sessionId, cwd, additionalDirectories));

    private SampleSession Require(string sessionId, string method) =>
        _sessions.TryGetValue(sessionId, out var session)
            ? session
            : throw new RpcException(-32602, $"Unknown sessionId '{sessionId}'.", method);

    /// <summary>
    /// The advertised config options for a client, honoring the requirement that a
    /// `type: "boolean"` option is only sent once the client advertised
    /// `clientCapabilities.session.configOptions.boolean`.
    /// </summary>
    private List<SessionConfigOption> VisibleConfigOptions(SampleSession session)
    {
        List<SessionConfigOption> options =
        [
            new SessionConfigOption(new SessionConfigSelect
            {
                Id = VerbosityConfigId,
                Name = "Verbosity",
                CurrentValue = session.Verbosity,
                Options = new List<SessionConfigSelectOption>
                {
                    new() { Value = "quiet", Name = "Quiet" },
                    new() { Value = "normal", Name = "Normal" },
                    new() { Value = "verbose", Name = "Verbose" }
                }
            })
        ];

        if (_clientSupportsBooleanConfigOptions)
        {
            options.Add(new SessionConfigOption(new SessionConfigBoolean
            {
                Id = ConfirmConfigId,
                Name = "Confirm actions",
                CurrentValue = session.ConfirmActions
            }));
        }

        return options;
    }
}
