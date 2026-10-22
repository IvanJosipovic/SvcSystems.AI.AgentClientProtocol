using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger? _logger;
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
        _logger = options.LoggerFactory?.CreateLogger<Client>();
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
        // Surface the terminal failure without requiring callers to observe Completion. The continuation
        // runs on the thread pool rather than the receive loop so a slow handler cannot stall teardown.
        // CompletedTask only ever reports a result (never faults), but Status is checked rather than
        // Result so this continuation can never observe an exception and fault unobserved.
        _ = _connection.Completion.ContinueWith(
            static (completion, state) =>
            {
                if (completion.Status == TaskStatus.RanToCompletion && completion.Result is { } failure)
                {
                    ((Client)state!).RaiseTransportFaulted(failure);
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    /// <summary>Negotiated agent identity and capabilities.</summary>
    public InitializeResponse Agent { get; private set; } = null!;

    /// <summary>Completes when the underlying connection closes; the result contains the transport failure, if any.</summary>
    public Task<Exception?> Completion => _connection.Completion;

    /// <inheritdoc />
    public event EventHandler<TransportFaultedEventArgs>? TransportFaulted;

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
            // A vendor extension must not be able to terminate the connection with a payload this
            // client cannot interpret. An unexpected shape is reported and ignored.
            try
            {
                var status = WireJson.ReadAuthenticationStatus(parameters);
                Volatile.Write(ref _authenticationStatus, status);
                _options.AuthenticationStatusChanged?.Invoke(status);
            }
            catch (ProtocolException exception)
            {
                _logger?.LogWarning(exception, "An ACP authentication status update was ignored because it was malformed.");
            }
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
                clientOptions.RequestHandlerException,
                clientOptions.LoggerFactory);
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
                clientOptions.RequestHandlerException,
                clientOptions.LoggerFactory);
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
        // The session is registered from the response observer, which the connection runs on the
        // receive loop before it delivers the result. An agent that answers session/new and then
        // streams an update for the new session therefore cannot lose that update to a race
        // between the response arriving and the session being registered.
        Session? registered = null;
        string? duplicateSessionId = null;
        var response = await _connection.SendRequestAsync(
            Methods.SessionNew,
            WireJson.WriteNewSessionRequest(request),
            cancellationToken,
            result =>
            {
                try
                {
                    registered = RegisterSession(result, fileSystemRoots);
                }
                catch (DuplicateSessionIdException exception)
                {
                    // Recorded here so the agent-side cleanup can be awaited on the caller's
                    // thread; the receive loop must not block on a transport write.
                    duplicateSessionId = exception.SessionId;
                }
            }).ConfigureAwait(false);

        if (duplicateSessionId is not null)
        {
            await ReleaseDuplicateSessionAsync(duplicateSessionId, cancellationToken).ConfigureAwait(false);
            throw new ProtocolException($"ACP agent returned duplicate session id '{duplicateSessionId}'.");
        }

        return registered ?? RegisterSession(response, fileSystemRoots);
    }

    /// <summary>Registers a live session so that its updates are routed to it.</summary>
    private Session RegisterSession(JsonElement response, IReadOnlyList<string>? fileSystemRoots)
    {
        var typedResponse = response.Deserialize(JsonContext.Default.NewSessionResponse)
            ?? throw new ProtocolException("The ACP agent returned an empty session/new response.");
        var session = new Session(this, typedResponse.SessionId, _options.MaximumBufferedSessionUpdates);
        session.SetInitialState(typedResponse.Modes, typedResponse.ConfigOptions, response);
        if (!_sessions.TryAdd(session.Id, session))
        {
            throw new DuplicateSessionIdException(session.Id);
        }
        if (fileSystemRoots is not null) _fileSystemRoots[session.Id] = fileSystemRoots;
        return session;
    }

    /// <summary>
    /// Releases an agent-side session whose id duplicates a live session.
    /// </summary>
    /// <remarks>
    /// The request is sent directly rather than through <see cref="CloseSessionAsync"/> so that the
    /// existing local session entry, which still owns that id, is left untouched. Best effort: the
    /// agent may not advertise close, in which case it answers with an error that is ignored.
    /// </remarks>
    private async Task ReleaseDuplicateSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            _ = await _connection.SendRequestAsync(
                Methods.SessionClose,
                JsonSerializer.SerializeToUtf8Bytes(new CloseSessionRequest { SessionId = sessionId }, JsonContext.Default.CloseSessionRequest),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The duplicate session id is the failure that is reported to the caller.
        }
    }

    private sealed class DuplicateSessionIdException(string sessionId)
        : Exception($"ACP agent returned duplicate session id '{sessionId}'.")
    {
        public string SessionId { get; } = sessionId;
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
            var typedResponse = response.Deserialize(JsonContext.Default.LoadSessionResponse)!;
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
        _ = await _connection.SendRequestAsync(
            Methods.Authenticate,
            JsonSerializer.SerializeToUtf8Bytes(new AuthenticateRequest { MethodId = methodId }, JsonContext.Default.AuthenticateRequest),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Agent.AgentCapabilities?.Auth?.Logout is null) throw new NotSupportedException("The ACP agent does not advertise logout support.");
        _ = await _connection.SendRequestAsync(Methods.Logout, JsonSerializer.SerializeToUtf8Bytes(new LogoutRequest(), JsonContext.Default.LogoutRequest), cancellationToken).ConfigureAwait(false);
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
            JsonSerializer.SerializeToUtf8Bytes(new ListSessionsRequest { Cwd = workingDirectory, Cursor = cursor }, JsonContext.Default.ListSessionsRequest),
            cancellationToken).ConfigureAwait(false);
        return response.Deserialize(JsonContext.Default.ListSessionsResponse)!;
    }

    /// <inheritdoc />
    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (Agent.AgentCapabilities?.SessionCapabilities?.Delete is null) throw new NotSupportedException("The ACP agent does not advertise session deletion.");
        _ = await _connection.SendRequestAsync(
            Methods.SessionDelete,
            JsonSerializer.SerializeToUtf8Bytes(new DeleteSessionRequest { SessionId = sessionId }, JsonContext.Default.DeleteSessionRequest),
            cancellationToken).ConfigureAwait(false);
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
            var typedResponse = response.Deserialize(JsonContext.Default.ResumeSessionResponse)!;
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
        _ = await _connection.SendRequestAsync(
            Methods.SessionClose,
            JsonSerializer.SerializeToUtf8Bytes(new CloseSessionRequest { SessionId = sessionId }, JsonContext.Default.CloseSessionRequest),
            cancellationToken).ConfigureAwait(false);
        _sessions.TryRemove(sessionId, out _);
        _fileSystemRoots.TryRemove(sessionId, out _);
    }

    internal async Task SetSessionModeAsync(string sessionId, string modeId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(modeId);
        _ = await _connection.SendRequestAsync(
            Methods.SessionSetMode,
            JsonSerializer.SerializeToUtf8Bytes(new SetSessionModeRequest { SessionId = sessionId, ModeId = modeId }, JsonContext.Default.SetSessionModeRequest),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<SessionConfigOption>> SetSessionConfigOptionAsync(SetSessionConfigOptionRequest value, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var response = await _connection.SendRequestAsync(Methods.SessionSetConfigOption, WireJson.WriteSetConfigOptionRequest(value), cancellationToken).ConfigureAwait(false);
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
        await _connection.SendNotificationAsync(Methods.SessionCancel, JsonSerializer.SerializeToUtf8Bytes(notification, JsonContext.Default.CancelNotification), cancellationToken).ConfigureAwait(false);
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
        Agent = response.Deserialize(JsonContext.Default.InitializeResponse)
            ?? throw new ProtocolException("The ACP agent returned an empty initialize response.");
        if (Agent.ProtocolVersion != ProtocolVersion)
        {
            throw new ProtocolException($"ACP protocol version {Agent.ProtocolVersion} was negotiated, but this client supports version {ProtocolVersion}.");
        }
    }

    private async ValueTask<byte[]> HandlePermissionRequestAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var request = parameters.Deserialize(JsonContext.Default.RequestPermissionRequest)
            ?? throw new RpcException(-32602, "A session/request_permission request must include its parameters.", Methods.SessionRequestPermission);
        // A permission request must belong to a session this client tracks. Otherwise a rogue or
        // confused agent could obtain a decision for a session the host never opened.
        if (!_sessions.ContainsKey(request.SessionId))
        {
            throw new RpcException(-32602, "Permission requests must reference an active session.", Methods.SessionRequestPermission);
        }
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
                var request = parameters.Deserialize(JsonContext.Default.ReadTextFileRequest)!;
                ValidateAbsolutePath(request.Path, "path");
                ValidateFileSystemPath(request.SessionId, request.Path, Methods.FsReadTextFile);
                var content = await fileSystem.ReadTextFileAsync(request, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToUtf8Bytes(new ReadTextFileResponse { Content = content }, JsonContext.Default.ReadTextFileResponse);
            };
            requests[Methods.FsWriteTextFile] = async (parameters, cancellationToken) =>
            {
                var request = parameters.Deserialize(JsonContext.Default.WriteTextFileRequest)!;
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
                var request = parameters.Deserialize(JsonContext.Default.CreateTerminalRequest)!;
                // The terminal's working directory must be absolute. Its security policy -- which
                // directories and commands are permitted -- belongs to the configured terminal
                // handler; the client only guarantees that the path is well formed.
                if (request.Cwd is not null) ValidateAbsolutePath(request.Cwd, "cwd");
                if (string.IsNullOrWhiteSpace(request.Command)) throw new RpcException(-32602, "Terminal command must not be empty.", Methods.TerminalCreate);
                var terminalId = await terminal.CreateAsync(request, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToUtf8Bytes(new CreateTerminalResponse { TerminalId = terminalId }, JsonContext.Default.CreateTerminalResponse);
            };
            requests[Methods.TerminalOutput] = async (parameters, cancellationToken) =>
            {
                var request = parameters.Deserialize(JsonContext.Default.TerminalOutputRequest)!;
                var output = await terminal.GetOutputAsync(request, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToUtf8Bytes(output, JsonContext.Default.TerminalOutputResponse);
            };
            requests[Methods.TerminalWaitForExit] = async (parameters, cancellationToken) =>
            {
                var request = parameters.Deserialize(JsonContext.Default.WaitForTerminalExitRequest)!;
                var status = await terminal.WaitForExitAsync(request, cancellationToken).ConfigureAwait(false);
                return WireJson.WriteTerminalExitStatusResponse(status);
            };
            requests[Methods.TerminalKill] = async (parameters, cancellationToken) =>
            {
                var request = parameters.Deserialize(JsonContext.Default.KillTerminalRequest)!;
                await terminal.KillAsync(request, cancellationToken).ConfigureAwait(false);
                return WireJson.WriteEmptyObject();
            };
            requests[Methods.TerminalRelease] = async (parameters, cancellationToken) =>
            {
                var request = parameters.Deserialize(JsonContext.Default.ReleaseTerminalRequest)!;
                await terminal.ReleaseAsync(request, cancellationToken).ConfigureAwait(false);
                return WireJson.WriteEmptyObject();
            };
        }

        if (_options.ElicitationHandler is { } elicitation)
        {
            requests[Methods.ElicitationCreate] = async (parameters, cancellationToken) =>
            {
                // The mode is checked before the payload is bound so an unsupported mode is reported as
                // method-not-found even when the request does not carry that mode's required members.
                var mode = WireJson.ReadElicitationMode(parameters);
                if (mode == "form" && !elicitation.SupportsForm || mode == "url" && !elicitation.SupportsUrl)
                {
                    throw new RpcException(-32601, $"Elicitation mode '{mode}' is not supported by this client.", Methods.ElicitationCreate);
                }

                var request = WireJson.ReadElicitationRequest(parameters);
                var response = await elicitation.RequestElicitationAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.Value is CreateElicitationResponseCase3 extension && !extension.Action.StartsWith('_'))
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
        // Prompt suggestions are a vendor extension. A payload this client cannot interpret is
        // reported and ignored rather than failing the update or the connection.
        var suggestions = update.Value is SessionInfoUpdate sessionInfo
            ? ReadPromptSuggestionsSafely(sessionInfo.Meta)
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

    private IReadOnlyList<string>? ReadPromptSuggestionsSafely(IReadOnlyDictionary<string, JsonElement>? meta)
    {
        try
        {
            return WireJson.ReadPromptSuggestions(meta);
        }
        catch (ProtocolException exception)
        {
            _logger?.LogWarning(exception, "ACP prompt suggestions were ignored because they were malformed.");
            return null;
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

    private void RaiseTransportFaulted(Exception failure)
    {
        // A fault can race with disposal; do not notify subscribers after the client is torn down.
        if (Volatile.Read(ref _disposed) != 0) return;
        if (TransportFaulted is not { } subscribers) return;
        var args = new TransportFaultedEventArgs(failure);
        // Invoke each subscriber in isolation: a throwing handler must not prevent the remaining
        // subscribers from learning that the connection has gone away.
        foreach (var subscriber in subscribers.GetInvocationList())
        {
            try
            {
                ((EventHandler<TransportFaultedEventArgs>)subscriber).Invoke(this, args);
            }
            catch (Exception handlerException)
            {
                try
                {
                    _logger?.LogWarning(handlerException, "An ACP transport-fault handler threw.");
                }
                catch
                {
                    // A failing diagnostic sink must never fault connection teardown.
                }
            }
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
