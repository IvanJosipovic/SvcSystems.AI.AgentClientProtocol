using System.Collections.Concurrent;
using System.Text.Json;
using SvcSystems.AI.AgentClientProtocol.JsonRpc;
using SvcSystems.AI.AgentClientProtocol.Serialization;
using SvcSystems.AI.AgentClientProtocol.Transport;

namespace SvcSystems.AI.AgentClientProtocol.Client;

/// <summary>Starts and owns an ACP agent process over stdio.</summary>
public sealed class Client : IClient
{
    private const int ProtocolVersion = 1;
    private readonly JsonRpcConnection _connection;
    private readonly ClientOptions _options;
    private readonly ProcessOptions? _processOptions;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _fileSystemRoots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activePromptCancellation = new(StringComparer.Ordinal);
    private AuthenticationStatus? _authenticationStatus;
    private int _disposed;

    private Client(JsonRpcConnection connection, ProcessOptions? processOptions, ClientOptions options)
    {
        if (options.MaximumBufferedSessionUpdates <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum buffered session update count must be positive.");
        }
        if (options.MaximumConcurrentIncomingRequests <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum concurrent incoming request count must be positive.");
        }
        _connection = connection;
        _processOptions = processOptions;
        _options = options;
        RegisterHostCallbacks();
        foreach (var (method, handler) in options.ExtensionRequestHandlers)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(method);
            ArgumentNullException.ThrowIfNull(handler);
            if (!method.StartsWith('_'))
                throw new ArgumentException("Incoming extension request method names must begin with an underscore.", nameof(options));
            _connection.RegisterRequestHandler(method, async (parameters, cancellationToken) =>
            {
                if (parameters.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                    throw new RpcException(-32602, "JSON-RPC parameters must be an object or array.", method);
                return WireJson.WriteJsonValue(await handler(parameters, cancellationToken).ConfigureAwait(false));
            });
        }
        _connection.UnhandledNotificationReceived = HandleExtensionNotification;
    }

    /// <summary>Negotiated agent identity and capabilities.</summary>
    public InitializeResponse Agent { get; private set; } = null!;

    /// <summary>Completes when the underlying connection closes; the result contains the transport failure, if any.</summary>
    public Task<Exception?> Completion => _connection.Completion;

    /// <inheritdoc />
    public AuthenticationStatus? AuthenticationStatus => Volatile.Read(ref _authenticationStatus);

    /// <inheritdoc />
    public Task<JsonElement> SendExtensionRequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateExtensionParameters(method, parameters);
        return _connection.SendRequestAsync(method, WireJson.WriteJsonValue(parameters), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask SendExtensionNotificationAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateExtensionParameters(method, parameters);
        return _connection.SendNotificationAsync(method, WireJson.WriteJsonValue(parameters), cancellationToken);
    }

    private static void ValidateExtensionParameters(string method, JsonElement parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (parameters.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            throw new ArgumentException("JSON-RPC parameters must be an object or array. Use an empty object for parameterless methods.", nameof(parameters));
    }

    private void HandleExtensionNotification(string method, JsonElement parameters)
    {
        if (method == "_auth/status_update")
        {
            var status = WireJson.ReadAuthenticationStatus(parameters);
            Volatile.Write(ref _authenticationStatus, status);
            _options.AuthenticationStatusChanged?.Invoke(status);
        }
        _options.ExtensionNotificationReceived?.Invoke(new ExtensionNotification(method, parameters));
    }

    /// <summary>Starts an agent process and completes the ACP initialize handshake.</summary>
    public static async Task<Client> StartAsync(
        ProcessOptions processOptions,
        ClientOptions? clientOptions = null,
        CancellationToken cancellationToken = default)
    {
        clientOptions ??= new ClientOptions();
        var transport = await StdioTransport.StartAsync(processOptions, cancellationToken).ConfigureAwait(false);
        JsonRpcConnection? connection = null;
        try
        {
            connection = new JsonRpcConnection(
                transport,
                clientOptions.MaximumConcurrentIncomingRequests,
                clientOptions.RequestHandlerException);
            var client = new Client(connection, processOptions, clientOptions);
            await client.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    /// <summary>Connects over a caller-provided transport and completes the ACP initialize handshake.</summary>
    /// <remarks>The client takes ownership of <paramref name="transport"/> and disposes it with the client.</remarks>
    public static async Task<Client> ConnectAsync(
        ITransport transport,
        ClientOptions? clientOptions = null,
        CancellationToken cancellationToken = default,
        ProcessOptions? terminalAuthenticationProcessOptions = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        clientOptions ??= new ClientOptions();
        JsonRpcConnection? connection = null;
        try
        {
            connection = new JsonRpcConnection(
                transport,
                clientOptions.MaximumConcurrentIncomingRequests,
                clientOptions.RequestHandlerException);
            var client = new Client(connection, terminalAuthenticationProcessOptions, clientOptions);
            await client.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            else await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Session> CreateSessionAsync(SessionOptions options, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(options);
        ValidateSessionOptions(options);
        var fileSystemRoots = GetFileSystemRoots(options);

        var request = new NewSessionRequest
        {
            Cwd = options.WorkingDirectory,
            McpServers = [.. options.McpServers],
            AdditionalDirectories = options.AdditionalDirectories.Count > 0 ? [.. options.AdditionalDirectories] : null,
            Meta = WireJson.CopyMetadata(options.Metadata)
        };
        var response = await _connection.SendRequestAsync(
            Methods.SessionNew,
            WireJson.WriteNewSessionRequest(request),
            cancellationToken).ConfigureAwait(false);
        var typedResponse = WireJson.ReadNewSessionResponse(response);
        var session = new Session(this, typedResponse.SessionId, _options.MaximumBufferedSessionUpdates);
        session.SetInitialState(typedResponse.Modes, typedResponse.ConfigOptions, response);
        if (!_sessions.TryAdd(session.Id, session))
        {
            // The agent created a second session whose id is already live. Release the agent-side
            // session before failing so that the duplicate does not leak agent state. The request is
            // sent directly (rather than through CloseSessionAsync) so that the existing local session
            // entry, which still owns that id, is left untouched. Best effort: the agent may not
            // advertise close, in which case it answers with an error that is intentionally ignored.
            try
            {
                _ = await _connection.SendRequestAsync(
                    Methods.SessionClose,
                    WireJson.WriteSessionIdRequest(session.Id),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The duplicate session id is the failure that is reported to the caller.
            }
            throw new ProtocolException($"ACP agent returned duplicate session id '{session.Id}'.");
        }
        if (fileSystemRoots is not null) _fileSystemRoots[session.Id] = fileSystemRoots;
        return session;
    }

    /// <inheritdoc />
    public async Task<Session> LoadSessionAsync(string sessionId, SessionOptions options, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(options);
        if (Agent.AgentCapabilities?.LoadSession != true) throw new NotSupportedException("The ACP agent does not advertise session loading.");
        ValidateSessionOptions(options);

        var fileSystemRoots = GetFileSystemRoots(options);
        var created = new Session(this, sessionId, _options.MaximumBufferedSessionUpdates);
        var session = _sessions.GetOrAdd(sessionId, created);
        if (ReferenceEquals(session, created) && fileSystemRoots is not null) _fileSystemRoots[sessionId] = fileSystemRoots;

        try
        {
            var request = new LoadSessionRequest
            {
                SessionId = sessionId,
                Cwd = options.WorkingDirectory,
                McpServers = [.. options.McpServers],
                AdditionalDirectories = options.AdditionalDirectories.Count > 0 ? [.. options.AdditionalDirectories] : null,
                Meta = WireJson.CopyMetadata(options.Metadata)
            };
            var response = await _connection.SendRequestAsync(Methods.SessionLoad, WireJson.WriteLoadSessionRequest(request), cancellationToken).ConfigureAwait(false);
            var typedResponse = WireJson.ReadLoadSessionResponse(response);
            session.SetInitialState(typedResponse.Modes, typedResponse.ConfigOptions, response);
            return session;
        }
        catch
        {
            if (ReferenceEquals(session, created))
            {
                _sessions.TryRemove(sessionId, out _);
                _fileSystemRoots.TryRemove(sessionId, out _);
            }
            throw;
        }
    }

    /// <inheritdoc />
    public async Task AuthenticateAsync(string methodId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(methodId);
        var method = Agent.AuthMethods?.FirstOrDefault(method => method switch
        {
            AuthMethodAgent agent => string.Equals(agent.Id, methodId, StringComparison.Ordinal),
            AuthMethodTerminal terminal => string.Equals(terminal.Id, methodId, StringComparison.Ordinal),
            _ => false
        });
        if (method is null) throw new ArgumentException($"Authentication method '{methodId}' was not advertised by the ACP agent.", nameof(methodId));
        if (method is AuthMethodTerminal terminalMethod)
        {
            var processOptions = _processOptions
                ?? throw new NotSupportedException("Terminal authentication requires process options and is unavailable for a caller-provided transport.");
            var handler = _options.TerminalAuthenticationHandler
                ?? throw new NotSupportedException("This agent requires terminal authentication but no terminal authentication handler is configured.");
            var environment = processOptions.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (terminalMethod.Env is not null)
            {
                foreach (var variable in terminalMethod.Env) environment[variable.Key] = variable.Value;
            }
            var arguments = processOptions.Arguments.Concat(terminalMethod.Args ?? []).ToArray();
            var exitCode = await handler.RunAsync(
                new TerminalAuthenticationRequest(processOptions.FileName, arguments, processOptions.WorkingDirectory ?? Environment.CurrentDirectory, environment),
                cancellationToken).ConfigureAwait(false);
            if (exitCode != 0) throw new ProcessException($"Terminal authentication exited with code {exitCode}.");
            return;
        }
        _ = await _connection.SendRequestAsync(Methods.Authenticate, WireJson.WriteAuthenticateRequest(methodId), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Agent.AgentCapabilities?.Auth?.Logout is null) throw new NotSupportedException("The ACP agent does not advertise logout support.");
        _ = await _connection.SendRequestAsync(Methods.Logout, WireJson.WriteLogoutRequest(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(
        string? workingDirectory = null,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        (await ListSessionsPageAsync(workingDirectory, cursor, cancellationToken).ConfigureAwait(false)).Sessions;

    /// <inheritdoc />
    public async Task<ListSessionsResponse> ListSessionsPageAsync(
        string? workingDirectory = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Agent.AgentCapabilities?.SessionCapabilities?.List is null) throw new NotSupportedException("The ACP agent does not advertise session listing.");
        if (workingDirectory is not null) ValidateWorkingDirectory(workingDirectory);
        var response = await _connection.SendRequestAsync(
            Methods.SessionList,
            WireJson.WriteListSessionsRequest(workingDirectory, cursor),
            cancellationToken).ConfigureAwait(false);
        return WireJson.ReadSessionListPage(response);
    }

    /// <inheritdoc />
    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (Agent.AgentCapabilities?.SessionCapabilities?.Delete is null) throw new NotSupportedException("The ACP agent does not advertise session deletion.");
        _ = await _connection.SendRequestAsync(Methods.SessionDelete, WireJson.WriteSessionIdRequest(sessionId), cancellationToken).ConfigureAwait(false);
        _sessions.TryRemove(sessionId, out _);
        _fileSystemRoots.TryRemove(sessionId, out _);
    }

    /// <inheritdoc />
    public async Task<Session> ResumeSessionAsync(string sessionId, SessionOptions options, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(options);
        if (Agent.AgentCapabilities?.SessionCapabilities?.Resume is null) throw new NotSupportedException("The ACP agent does not advertise session resume.");
        ValidateSessionOptions(options);

        var fileSystemRoots = GetFileSystemRoots(options);
        var created = new Session(this, sessionId, _options.MaximumBufferedSessionUpdates);
        var session = _sessions.GetOrAdd(sessionId, created);
        if (ReferenceEquals(session, created) && fileSystemRoots is not null) _fileSystemRoots[sessionId] = fileSystemRoots;
        try
        {
            var response = await _connection.SendRequestAsync(Methods.SessionResume, WireJson.WriteResumeSessionRequest(sessionId, options), cancellationToken).ConfigureAwait(false);
            var typedResponse = WireJson.ReadResumeSessionResponse(response);
            session.SetInitialState(typedResponse.Modes, typedResponse.ConfigOptions, response);
            return session;
        }
        catch
        {
            if (ReferenceEquals(session, created))
            {
                _sessions.TryRemove(sessionId, out _);
                _fileSystemRoots.TryRemove(sessionId, out _);
            }
            throw;
        }
    }

    /// <inheritdoc />
    public async Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (Agent.AgentCapabilities?.SessionCapabilities?.Close is null) throw new NotSupportedException("The ACP agent does not advertise session close.");
        _ = await _connection.SendRequestAsync(Methods.SessionClose, WireJson.WriteSessionIdRequest(sessionId), cancellationToken).ConfigureAwait(false);
        _sessions.TryRemove(sessionId, out _);
        _fileSystemRoots.TryRemove(sessionId, out _);
    }

    internal async Task SetSessionModeAsync(string sessionId, string modeId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(modeId);
        _ = await _connection.SendRequestAsync(Methods.SessionSetMode, WireJson.WriteSetModeRequest(sessionId, modeId), cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<SessionConfigOption>> SetSessionConfigOptionAsync(string sessionId, string configId, SetSessionConfigOptionRequest value, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(configId);
        if (value.Value is not (SetSessionConfigOptionRequestCase0 or SetSessionConfigOptionRequestCase1))
            throw new ArgumentException("A string or boolean session configuration value is required.", nameof(value));
        var response = await _connection.SendRequestAsync(Methods.SessionSetConfigOption, WireJson.WriteSetConfigOptionRequest(sessionId, configId, value), cancellationToken).ConfigureAwait(false);
        return WireJson.ReadConfigOptionsResponse(response);
    }

    internal async Task<PromptResponse> PromptAsync(string sessionId, IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken, PromptOptions? options = null)
    {
        ThrowIfDisposed();
        ValidatePromptContent(content);
        // Session serializes prompts per session, so at most one is active at a time. Registering the
        // cancellation source lets a pending permission request observe prompt cancellation.
        var permissionCancellation = new CancellationTokenSource();
        _activePromptCancellation[sessionId] = permissionCancellation;
        try
        {
            var response = await _connection.SendRequestAsync(Methods.SessionPrompt, WireJson.WritePromptRequest(sessionId, content, options), cancellationToken).ConfigureAwait(false);
            return response.Deserialize(JsonContext.Default.PromptResponse)!;
        }
        finally
        {
            _activePromptCancellation.TryRemove(sessionId, out _);
            permissionCancellation.Dispose();
        }
    }

    internal async Task CancelSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_activePromptCancellation.TryGetValue(sessionId, out var permissionCancellation))
        {
            try { await permissionCancellation.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }
        var notification = new CancelNotification { SessionId = sessionId };
        await _connection.SendNotificationAsync(Methods.SessionCancel, WireJson.WriteCancelNotification(notification), cancellationToken).ConfigureAwait(false);
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var clientCapabilities = new ClientCapabilities
        {
            Fs = new FileSystemCapabilities
            {
                ReadTextFile = _options.FileSystemHandler is not null,
                WriteTextFile = _options.FileSystemHandler is not null
            },
            Terminal = _options.TerminalHandler is not null,
            Auth = new AuthCapabilities { Terminal = _options.TerminalAuthenticationHandler is not null },
            Meta = WireJson.CopyMetadata(_options.CapabilityMetadata)
        };
        var request = new InitializeRequest
        {
            ProtocolVersion = ProtocolVersion,
            ClientCapabilities = clientCapabilities,
            Meta = WireJson.CopyMetadata(_options.Metadata)
        };

        var response = await _connection.SendRequestAsync(Methods.Initialize, WireJson.WriteInitializeRequest(request, _options.ClientInfo, _options), cancellationToken).ConfigureAwait(false);
        Agent = response.Deserialize(JsonContext.Default.InitializeResponse)!;
        if (Agent.ProtocolVersion != ProtocolVersion)
        {
            throw new ProtocolException($"ACP protocol version {Agent.ProtocolVersion} was negotiated, but this client supports version {ProtocolVersion}.");
        }
    }

    private async ValueTask<byte[]> HandlePermissionRequestAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var request = WireJson.ReadPermissionRequest(parameters);
        PermissionDecision decision;
        var promptCancellation = _activePromptCancellation.TryGetValue(request.SessionId, out var activeCancellation)
            ? activeCancellation.Token
            : CancellationToken.None;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, promptCancellation);
        if (linkedCancellation.IsCancellationRequested)
        {
            decision = PermissionDecision.Cancel();
        }
        else if (_options.PermissionHandler is not null)
        {
            try
            {
                decision = await _options.PermissionHandler.RequestPermissionAsync(request, linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
                decision = PermissionDecision.Cancel();
            }
        }
        else
        {
            var rejectOption = request.Options.FirstOrDefault(option => option.Kind.StartsWith("reject", StringComparison.Ordinal));
            if (rejectOption is null)
            {
                throw new RpcException(-32601, "No permission handler is configured and the agent supplied no reject option.", Methods.SessionRequestPermission);
            }
            decision = PermissionDecision.Select(rejectOption.OptionId);
        }

        if (linkedCancellation.IsCancellationRequested) decision = PermissionDecision.Cancel();
        if (!decision.Cancelled && (decision.SelectedOptionId is null || !request.Options.Any(option => option.OptionId == decision.SelectedOptionId)))
        {
            throw new RpcException(-32602, "Permission handler returned an option id that was not offered by the agent.", Methods.SessionRequestPermission);
        }

        return WireJson.WritePermissionResponse(decision);
    }

    private void RegisterHostCallbacks()
    {
        var requests = new Dictionary<string, Func<JsonElement, CancellationToken, ValueTask<byte[]>>>(StringComparer.Ordinal)
        {
            [Methods.SessionRequestPermission] = HandlePermissionRequestAsync
        };
        var notifications = new Dictionary<string, Func<JsonElement, ValueTask>>(StringComparer.Ordinal)
        {
            [Methods.SessionUpdate] = HandleSessionUpdateAsync
        };

        if (_options.FileSystemHandler is { } fileSystem)
        {
            requests[Methods.FsReadTextFile] = async (parameters, cancellationToken) =>
            {
                var request = WireJson.ReadTextFileReadRequest(parameters);
                ValidateAbsolutePath(request.Path, "path");
                ValidateFileSystemPath(request.SessionId, request.Path, Methods.FsReadTextFile);
                var content = await fileSystem.ReadTextFileAsync(request, cancellationToken).ConfigureAwait(false);
                return WireJson.WriteTextFileReadResponse(content);
            };
            requests[Methods.FsWriteTextFile] = async (parameters, cancellationToken) =>
            {
                var request = WireJson.ReadTextFileWriteRequest(parameters);
                ValidateAbsolutePath(request.Path, "path");
                ValidateFileSystemPath(request.SessionId, request.Path, Methods.FsWriteTextFile);
                await fileSystem.WriteTextFileAsync(request, cancellationToken).ConfigureAwait(false);
                return WireJson.WriteEmptyObject();
            };
        }

        if (_options.TerminalHandler is { } terminal)
        {
            requests[Methods.TerminalCreate] = async (parameters, cancellationToken) =>
            {
                var request = WireJson.ReadTerminalCreateRequest(parameters);
                if (request.Cwd is not null) ValidateAbsolutePath(request.Cwd, "cwd");
                if (string.IsNullOrWhiteSpace(request.Command)) throw new RpcException(-32602, "Terminal command must not be empty.", Methods.TerminalCreate);
                var terminalId = await terminal.CreateAsync(request, cancellationToken).ConfigureAwait(false);
                return WireJson.WriteTerminalCreateResponse(terminalId);
            };
            requests[Methods.TerminalOutput] = async (parameters, cancellationToken) =>
            {
                var request = WireJson.ReadTerminalOutputRequest(parameters);
                var output = await terminal.GetOutputAsync(request, cancellationToken).ConfigureAwait(false);
                return WireJson.WriteTerminalOutputResponse(output);
            };
            requests[Methods.TerminalWaitForExit] = async (parameters, cancellationToken) =>
            {
                var request = WireJson.ReadTerminalExitRequest(parameters);
                var status = await terminal.WaitForExitAsync(request, cancellationToken).ConfigureAwait(false);
                return WireJson.WriteTerminalExitStatusResponse(status);
            };
            requests[Methods.TerminalKill] = async (parameters, cancellationToken) =>
            {
                var request = WireJson.ReadTerminalKillRequest(parameters);
                await terminal.KillAsync(request, cancellationToken).ConfigureAwait(false);
                return WireJson.WriteEmptyObject();
            };
            requests[Methods.TerminalRelease] = async (parameters, cancellationToken) =>
            {
                var request = WireJson.ReadTerminalReleaseRequest(parameters);
                await terminal.ReleaseAsync(request, cancellationToken).ConfigureAwait(false);
                return WireJson.WriteEmptyObject();
            };
        }

        if (_options.ElicitationHandler is { } elicitation)
        {
            requests[Methods.ElicitationCreate] = async (parameters, cancellationToken) =>
            {
                var request = WireJson.ReadElicitationRequest(parameters);
                if (request.Mode == "form" && !elicitation.SupportsForm || request.Mode == "url" && !elicitation.SupportsUrl)
                {
                    throw new RpcException(-32601, $"Elicitation mode '{request.Mode}' is not supported by this client.", Methods.ElicitationCreate);
                }

                var response = await elicitation.RequestElicitationAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.Action is not ("accept" or "decline" or "cancel") && !response.Action.StartsWith('_'))
                {
                    throw new RpcException(-32602, "Elicitation handler returned an unsupported action.", Methods.ElicitationCreate);
                }
                return WireJson.WriteElicitationResponse(response);
            };
        }

        notifications[Methods.ElicitationComplete] = parameters =>
        {
            _options.ElicitationCompleted?.Invoke(WireJson.ReadElicitationCompleted(parameters));
            return ValueTask.CompletedTask;
        };

        foreach (var (method, handler) in requests) _connection.RegisterRequestHandler(method, handler);
        foreach (var (method, handler) in notifications) _connection.RegisterNotificationHandler(method, handler);
    }

    private ValueTask HandleSessionUpdateAsync(JsonElement parameters)
    {
        var sessionId = WireJson.ReadNotificationSessionId(parameters);
        if (!_sessions.TryGetValue(sessionId, out var session)) return ValueTask.CompletedTask;
        var update = WireJson.ReadSessionUpdate(parameters);
        var published = session.Publish(update);
        var suggestions = update.Value is SessionInfoUpdate sessionInfo
            ? WireJson.ReadPromptSuggestions(sessionInfo.Meta)
            : null;
        if (suggestions is not null)
            _options.PromptSuggestionsChanged?.Invoke(sessionId, suggestions);
        if (!published)
        {
            // Notification handlers run inline on the receive loop, so this one must not await a
            // transport send: a peer that has stopped reading its stdin would stall response
            // processing for the whole connection. The update stream already carries the failure,
            // so cancellation is best effort and detached from the dispatch path.
            _ = CancelSessionBestEffortAsync(sessionId);
        }
        return ValueTask.CompletedTask;
    }

    private async Task CancelSessionBestEffortAsync(string sessionId)
    {
        try
        {
            await CancelSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The update stream already carries the failure; cancellation is best effort.
        }
    }

    private void ValidatePromptContent(IReadOnlyList<ContentBlock> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        foreach (var block in content)
        {
            if (block.Value is ImageContent && Agent.AgentCapabilities?.PromptCapabilities?.Image != true)
                throw new NotSupportedException("The ACP agent does not advertise image prompt support.");
            if (block.Value is AudioContent && Agent.AgentCapabilities?.PromptCapabilities?.Audio != true)
                throw new NotSupportedException("The ACP agent does not advertise audio prompt support.");
            if (block.Value is EmbeddedResource && Agent.AgentCapabilities?.PromptCapabilities?.EmbeddedContext != true)
                throw new NotSupportedException("The ACP agent does not advertise embedded context support.");
        }
    }

    private void ValidateSessionOptions(SessionOptions options)
    {
        ValidateWorkingDirectory(options.WorkingDirectory);
        ValidateAdditionalDirectories(options.AdditionalDirectories);
        ValidateMcpServers(options.McpServers, Agent.AgentCapabilities?.McpCapabilities);
        if (options.AdditionalDirectories.Count > 0 && Agent.AgentCapabilities?.SessionCapabilities?.AdditionalDirectories is null)
        {
            throw new NotSupportedException("The ACP agent does not advertise additionalDirectories support.");
        }
    }

    private IReadOnlyList<string>? GetFileSystemRoots(SessionOptions options) =>
        _options.FileSystemHandler is null
            ? null
            : WorkspacePathPolicy.CreateRoots(options.WorkingDirectory, options.AdditionalDirectories);

    private void ValidateFileSystemPath(string sessionId, string path, string method)
    {
        if (!_fileSystemRoots.TryGetValue(sessionId, out var roots))
        {
            throw new RpcException(-32602, "Filesystem access requires an active session with an authorized workspace.", method);
        }

        try
        {
            if (WorkspacePathPolicy.AllowsPath(path, roots)) return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            throw new RpcException(-32602, "Filesystem path could not be resolved safely.", method);
        }

        throw new RpcException(-32602, "Filesystem path is outside the session's authorized workspace.", method);
    }

    private static void ValidateWorkingDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("ACP session working directories must be absolute paths.", nameof(directory));
    }

    private static void ValidateAdditionalDirectories(IReadOnlyList<string> directories)
    {
        foreach (var directory in directories)
        {
            if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("ACP additional directories must be absolute paths.", nameof(directories));
        }
    }

    private static void ValidateMcpServers(IReadOnlyList<McpServer> servers, McpCapabilities? capabilities)
    {
        foreach (var server in servers)
        {
            switch (server)
            {
                case McpServerStdio stdio:
                    ArgumentException.ThrowIfNullOrWhiteSpace(stdio.Name);
                    ArgumentException.ThrowIfNullOrWhiteSpace(stdio.Command);
                    if (!Path.IsPathFullyQualified(stdio.Command)) throw new ArgumentException("MCP stdio command must be an absolute path.", nameof(servers));
                    break;
                case McpServerHttp http:
                    ArgumentException.ThrowIfNullOrWhiteSpace(http.Name);
                    if (!Uri.TryCreate(http.Url, UriKind.Absolute, out _)) throw new ArgumentException("MCP HTTP URL must be absolute.", nameof(servers));
                    if (capabilities?.Http != true) throw new NotSupportedException("The ACP agent does not advertise HTTP MCP support.");
                    break;
                case McpServerSse sse:
                    ArgumentException.ThrowIfNullOrWhiteSpace(sse.Name);
                    if (!Uri.TryCreate(sse.Url, UriKind.Absolute, out _)) throw new ArgumentException("MCP SSE URL must be absolute.", nameof(servers));
                    if (capabilities?.Sse != true) throw new NotSupportedException("The ACP agent does not advertise SSE MCP support.");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(servers), $"Unknown MCP server configuration '{server.GetType().Name}'.");
            }
        }
    }

    private static void ValidateAbsolutePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new RpcException(-32602, $"ACP {parameterName} must be an absolute path.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var cancellation in _activePromptCancellation.Values)
        {
            try { await cancellation.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }
        _fileSystemRoots.Clear();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
