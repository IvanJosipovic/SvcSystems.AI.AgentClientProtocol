using System.Collections.Concurrent;
using System.Text.Json;
using SvcSystems.AI.AgentClientProtocol.JsonRpc;
using SvcSystems.AI.AgentClientProtocol.Generated;
using SvcSystems.AI.AgentClientProtocol.Serialization;
using SvcSystems.AI.AgentClientProtocol.Transport;

namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Starts and owns an ACP agent process over stdio.</summary>
public sealed class Client : IClient
{
    private const int ProtocolVersion = 1;
    private readonly JsonRpcConnection _connection;
    private readonly ClientOptions _options;
    private readonly ProcessOptions? _processOptions;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activePromptCancellation = new(StringComparer.Ordinal);
    private int _disposed;

    private Client(JsonRpcConnection connection, ProcessOptions? processOptions, ClientOptions options)
    {
        if (options.MaximumBufferedSessionUpdates <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum buffered session update count must be positive.");
        }
        _connection = connection;
        _processOptions = processOptions;
        _options = options;
        RegisterHostCallbacks();
    }

    /// <summary>Negotiated agent identity and capabilities.</summary>
    public AgentInfo Agent { get; private set; } = null!;

    /// <summary>Completes when the underlying connection closes; the result contains the transport failure, if any.</summary>
    public Task<Exception?> Completion => _connection.Completion;

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
            connection = new JsonRpcConnection(transport);
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
            connection = new JsonRpcConnection(transport);
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

        var request = new NewSessionRequest
        {
            Cwd = options.WorkingDirectory,
            McpServers = [],
            AdditionalDirectories = options.AdditionalDirectories.Count > 0 ? [.. options.AdditionalDirectories] : null
        };
        var response = await _connection.SendRequestAsync(
            Methods.SessionNew,
            WireJson.WriteNewSessionRequest(request, options.McpServers),
            cancellationToken).ConfigureAwait(false);
        var session = new Session(this, WireJson.ReadSessionId(response), _options.MaximumBufferedSessionUpdates);
        WireJson.ApplySessionInitialState(session, response);
        if (!_sessions.TryAdd(session.Id, session))
        {
            throw new ProtocolException($"ACP agent returned duplicate session id '{session.Id}'.");
        }
        return session;
    }

    /// <inheritdoc />
    public async Task<Session> LoadSessionAsync(string sessionId, SessionOptions options, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(options);
        if (Agent.Capabilities.LoadSession != true) throw new NotSupportedException("The ACP agent does not advertise session loading.");
        ValidateSessionOptions(options);

        var created = new Session(this, sessionId, _options.MaximumBufferedSessionUpdates);
        var session = _sessions.GetOrAdd(sessionId, created);

        try
        {
            var request = new LoadSessionRequest
            {
                SessionId = sessionId,
                Cwd = options.WorkingDirectory,
                McpServers = [],
                AdditionalDirectories = options.AdditionalDirectories.Count > 0 ? [.. options.AdditionalDirectories] : null
            };
            var response = await _connection.SendRequestAsync(Methods.SessionLoad, WireJson.WriteLoadSessionRequest(request, options.McpServers), cancellationToken).ConfigureAwait(false);
            WireJson.ApplySessionInitialState(session, response);
            return session;
        }
        catch
        {
            if (ReferenceEquals(session, created)) _sessions.TryRemove(sessionId, out _);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task AuthenticateAsync(string methodId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(methodId);
        var method = Agent.AuthenticationMethods?.FirstOrDefault(method => string.Equals(method.Id, methodId, StringComparison.Ordinal));
        if (method is null) throw new ArgumentException($"Authentication method '{methodId}' was not advertised by the ACP agent.", nameof(methodId));
        if (Discriminators.ParseAuthMethodDiscriminator(method.Type) == AuthMethodDiscriminator.Terminal)
        {
            var processOptions = _processOptions
                ?? throw new NotSupportedException("Terminal authentication requires process options and is unavailable for a caller-provided transport.");
            var handler = _options.TerminalAuthenticationHandler
                ?? throw new NotSupportedException("This agent requires terminal authentication but no terminal authentication handler is configured.");
            var environment = processOptions.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (method.Environment is not null)
            {
                foreach (var variable in method.Environment) environment[variable.Key] = variable.Value;
            }
            var arguments = processOptions.Arguments.Concat(method.Arguments ?? []).ToArray();
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
        if (Agent.Capabilities.Auth?.Logout is null) throw new NotSupportedException("The ACP agent does not advertise logout support.");
        _ = await _connection.SendRequestAsync(Methods.Logout, WireJson.WriteLogoutRequest(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(
        string? workingDirectory = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Agent.Capabilities.SessionCapabilities?.List is null) throw new NotSupportedException("The ACP agent does not advertise session listing.");
        if (workingDirectory is not null) ValidateWorkingDirectory(workingDirectory);
        return (await ListSessionsPageAsync(workingDirectory, cursor, cancellationToken).ConfigureAwait(false)).Sessions;
    }

    /// <inheritdoc />
    public async Task<ListSessionsResponse> ListSessionsPageAsync(
        string? workingDirectory = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Agent.Capabilities.SessionCapabilities?.List is null) throw new NotSupportedException("The ACP agent does not advertise session listing.");
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
        if (Agent.Capabilities.SessionCapabilities?.Delete is null) throw new NotSupportedException("The ACP agent does not advertise session deletion.");
        _ = await _connection.SendRequestAsync(Methods.SessionDelete, WireJson.WriteSessionIdRequest(sessionId), cancellationToken).ConfigureAwait(false);
        _sessions.TryRemove(sessionId, out _);
    }

    /// <inheritdoc />
    public async Task<Session> ResumeSessionAsync(string sessionId, SessionOptions options, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(options);
        if (Agent.Capabilities.SessionCapabilities?.Resume is null) throw new NotSupportedException("The ACP agent does not advertise session resume.");
        ValidateSessionOptions(options);

        var created = new Session(this, sessionId, _options.MaximumBufferedSessionUpdates);
        var session = _sessions.GetOrAdd(sessionId, created);
        try
        {
            var response = await _connection.SendRequestAsync(Methods.SessionResume, WireJson.WriteResumeSessionRequest(sessionId, options), cancellationToken).ConfigureAwait(false);
            WireJson.ApplySessionInitialState(session, response);
            return session;
        }
        catch
        {
            if (ReferenceEquals(session, created)) _sessions.TryRemove(sessionId, out _);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (Agent.Capabilities.SessionCapabilities?.Close is null) throw new NotSupportedException("The ACP agent does not advertise session close.");
        _ = await _connection.SendRequestAsync(Methods.SessionClose, WireJson.WriteSessionIdRequest(sessionId), cancellationToken).ConfigureAwait(false);
        _sessions.TryRemove(sessionId, out _);
    }

    internal async Task SetSessionModeAsync(string sessionId, string modeId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(modeId);
        _ = await _connection.SendRequestAsync(Methods.SessionSetMode, WireJson.WriteSetModeRequest(sessionId, modeId), cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<SessionConfigOption>> SetSessionConfigOptionAsync(string sessionId, string configId, ConfigValue value, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(configId);
        if ((value.ValueId is null) == (value.Boolean is null))
            throw new ArgumentException("Provide exactly one of a string value id or a boolean value.", nameof(value));
        var response = await _connection.SendRequestAsync(Methods.SessionSetConfigOption, WireJson.WriteSetConfigOptionRequest(sessionId, configId, value), cancellationToken).ConfigureAwait(false);
        return WireJson.ReadConfigOptionsResponse(response);
    }

    internal async Task<(string StopReason, JsonElement RawPayload)> PromptAsync(string sessionId, IReadOnlyList<AgentContentBlock> content, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidatePromptContent(content);
        var permissionCancellation = new CancellationTokenSource();
        if (!_activePromptCancellation.TryAdd(sessionId, permissionCancellation))
        {
            permissionCancellation.Dispose();
            throw new InvalidOperationException($"Session '{sessionId}' already has an active prompt.");
        }
        try
        {
            var response = await _connection.SendRequestAsync(Methods.SessionPrompt, WireJson.WritePromptRequest(sessionId, content), cancellationToken).ConfigureAwait(false);
            return (WireJson.ReadStopReason(response), response.Clone());
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
            Auth = new AuthCapabilities { Terminal = _options.TerminalAuthenticationHandler is not null }
        };
        var request = new InitializeRequest
        {
            ProtocolVersion = ProtocolVersion,
            ClientCapabilities = clientCapabilities
        };

        var response = await _connection.SendRequestAsync(Methods.Initialize, WireJson.WriteInitializeRequest(request, _options.ClientInfo, _options), cancellationToken).ConfigureAwait(false);
        Agent = WireJson.ReadAgentInfo(response);
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
                var content = await fileSystem.ReadTextFileAsync(request, cancellationToken).ConfigureAwait(false);
                return WireJson.WriteTextFileReadResponse(content);
            };
            requests[Methods.FsWriteTextFile] = async (parameters, cancellationToken) =>
            {
                var request = WireJson.ReadTextFileWriteRequest(parameters);
                ValidateAbsolutePath(request.Path, "path");
                await fileSystem.WriteTextFileAsync(request, cancellationToken).ConfigureAwait(false);
                return WireJson.WriteEmptyResponse();
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
                return WireJson.WriteEmptyResponse();
            };
            requests[Methods.TerminalRelease] = async (parameters, cancellationToken) =>
            {
                var request = WireJson.ReadTerminalReleaseRequest(parameters);
                await terminal.ReleaseAsync(request, cancellationToken).ConfigureAwait(false);
                return WireJson.WriteEmptyResponse();
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
        CallbackRoutes.Register(_connection, requests, notifications);
    }

    private async ValueTask HandleSessionUpdateAsync(JsonElement parameters)
    {
        var sessionId = WireJson.ReadNotificationSessionId(parameters);
        if (_sessions.TryGetValue(sessionId, out var session) && !session.Publish(WireJson.ReadSessionUpdate(parameters)))
        {
            try { await CancelSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false); }
            catch { /* The update stream already carries the failure; cancellation is best effort. */ }
        }
    }

    private void ValidatePromptContent(IReadOnlyList<AgentContentBlock> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        foreach (var block in content)
        {
            if (block is PromptImageContent && Agent.Capabilities.PromptCapabilities?.Image != true)
                throw new NotSupportedException("The ACP agent does not advertise image prompt support.");
            if (block is PromptAudioContent && Agent.Capabilities.PromptCapabilities?.Audio != true)
                throw new NotSupportedException("The ACP agent does not advertise audio prompt support.");
            if (block is EmbeddedResourceContent && Agent.Capabilities.PromptCapabilities?.EmbeddedContext != true)
                throw new NotSupportedException("The ACP agent does not advertise embedded context support.");
        }
    }

    private void ValidateSessionOptions(SessionOptions options)
    {
        ValidateWorkingDirectory(options.WorkingDirectory);
        ValidateAdditionalDirectories(options.AdditionalDirectories);
        ValidateMcpServers(options.McpServers, Agent.Capabilities.McpCapabilities);
        if (options.AdditionalDirectories.Count > 0 && Agent.Capabilities.SessionCapabilities?.AdditionalDirectories is null)
        {
            throw new NotSupportedException("The ACP agent does not advertise additionalDirectories support.");
        }
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

    private static void ValidateMcpServers(IReadOnlyList<McpServerOptions> servers, McpCapabilities? capabilities)
    {
        foreach (var server in servers)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(server.Name);
            switch (server)
            {
                case StdioMcpServerOptions stdio:
                    ArgumentException.ThrowIfNullOrWhiteSpace(stdio.Command);
                    if (!Path.IsPathFullyQualified(stdio.Command)) throw new ArgumentException("MCP stdio command must be an absolute path.", nameof(servers));
                    break;
                case HttpMcpServerOptions http:
                    if (!Uri.TryCreate(http.Url, UriKind.Absolute, out _)) throw new ArgumentException("MCP HTTP URL must be absolute.", nameof(servers));
                    if (capabilities?.Http != true) throw new NotSupportedException("The ACP agent does not advertise HTTP MCP support.");
                    break;
                case SseMcpServerOptions sse:
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var cancellation in _activePromptCancellation.Values)
        {
            try { await cancellation.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
