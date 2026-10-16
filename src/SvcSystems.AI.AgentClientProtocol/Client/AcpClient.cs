using System.Collections.Concurrent;
using System.Text.Json;
using SvcSystems.AI.AgentClientProtocol.JsonRpc;
using SvcSystems.AI.AgentClientProtocol.Protocol.Generated;
using SvcSystems.AI.AgentClientProtocol.Serialization;
using SvcSystems.AI.AgentClientProtocol.Transport;

namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Starts and owns an ACP agent process over stdio.</summary>
public sealed class AcpClient : IAcpClient
{
    private const int ProtocolVersion = 1;
    private readonly JsonRpcConnection _connection;
    private readonly AcpClientOptions _options;
    private readonly AcpProcessOptions _processOptions;
    private readonly ConcurrentDictionary<string, AcpSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activePromptCancellation = new(StringComparer.Ordinal);
    private int _disposed;

    private AcpClient(JsonRpcConnection connection, AcpProcessOptions processOptions, AcpClientOptions options)
    {
        _connection = connection;
        _processOptions = processOptions;
        _options = options;
        _connection.RegisterRequestHandler("session/request_permission", HandlePermissionRequestAsync);
        _connection.RegisterNotificationHandler("session/update", HandleSessionUpdateAsync);
        RegisterHostCallbacks();
    }

    /// <summary>Negotiated agent identity and capabilities.</summary>
    public AcpAgentInfo Agent { get; private set; } = null!;

    /// <summary>Starts an agent process and completes the ACP initialize handshake.</summary>
    public static async Task<AcpClient> StartAsync(
        AcpProcessOptions processOptions,
        AcpClientOptions? clientOptions = null,
        CancellationToken cancellationToken = default)
    {
        clientOptions ??= new AcpClientOptions();
        var transport = await StdioAcpTransport.StartAsync(processOptions, cancellationToken).ConfigureAwait(false);
        JsonRpcConnection? connection = null;
        try
        {
            connection = new JsonRpcConnection(transport);
            var client = new AcpClient(connection, processOptions, clientOptions);
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

    /// <inheritdoc />
    public async Task<AcpSession> CreateSessionAsync(AcpSessionOptions options, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(options);
        ValidateWorkingDirectory(options.WorkingDirectory);
        ValidateAdditionalDirectories(options.AdditionalDirectories);
        ValidateMcpServers(options.McpServers, Agent.Capabilities.Mcp);
        if (options.AdditionalDirectories.Count > 0 && !Agent.Capabilities.Sessions.AdditionalDirectories)
        {
            throw new NotSupportedException("The ACP agent does not advertise additionalDirectories support.");
        }

        var request = new NewSessionRequest
        {
            Cwd = options.WorkingDirectory,
            McpServers = [],
            AdditionalDirectories = options.AdditionalDirectories.Count > 0 ? [.. options.AdditionalDirectories] : null
        };
        var response = await _connection.SendRequestAsync(
            "session/new",
            AcpWireJson.WriteNewSessionRequest(request, options.McpServers),
            cancellationToken).ConfigureAwait(false);
        var session = new AcpSession(this, AcpWireJson.ReadSessionId(response));
        if (!_sessions.TryAdd(session.Id, session))
        {
            throw new AcpProtocolException($"ACP agent returned duplicate session id '{session.Id}'.");
        }
        return session;
    }

    /// <inheritdoc />
    public async Task<AcpSession> LoadSessionAsync(string sessionId, AcpSessionOptions options, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(options);
        if (!Agent.Capabilities.LoadSession) throw new NotSupportedException("The ACP agent does not advertise session loading.");
        ValidateWorkingDirectory(options.WorkingDirectory);
        ValidateAdditionalDirectories(options.AdditionalDirectories);
        ValidateMcpServers(options.McpServers, Agent.Capabilities.Mcp);
        if (options.AdditionalDirectories.Count > 0 && !Agent.Capabilities.Sessions.AdditionalDirectories)
        {
            throw new NotSupportedException("The ACP agent does not advertise additionalDirectories support.");
        }

        var created = new AcpSession(this, sessionId);
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
            _ = await _connection.SendRequestAsync("session/load", AcpWireJson.WriteLoadSessionRequest(request, options.McpServers), cancellationToken).ConfigureAwait(false);
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
        if (string.Equals(method.Type, "terminal", StringComparison.Ordinal))
        {
            var handler = _options.TerminalAuthenticationHandler
                ?? throw new NotSupportedException("This agent requires terminal authentication but no terminal authentication handler is configured.");
            var environment = _processOptions.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (method.Environment is not null)
            {
                foreach (var variable in method.Environment) environment[variable.Key] = variable.Value;
            }
            var arguments = _processOptions.Arguments.Concat(method.Arguments ?? []).ToArray();
            var exitCode = await handler.RunAsync(
                new AcpTerminalAuthenticationRequest(_processOptions.FileName, arguments, _processOptions.WorkingDirectory ?? Environment.CurrentDirectory, environment),
                cancellationToken).ConfigureAwait(false);
            if (exitCode != 0) throw new AcpProcessException($"Terminal authentication exited with code {exitCode}.");
            return;
        }
        _ = await _connection.SendRequestAsync("authenticate", AcpWireJson.WriteAuthenticateRequest(methodId), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!Agent.Capabilities.Logout) throw new NotSupportedException("The ACP agent does not advertise logout support.");
        _ = await _connection.SendRequestAsync("logout", AcpWireJson.WriteLogoutRequest(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AcpSessionInfo>> ListSessionsAsync(
        string? workingDirectory = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!Agent.Capabilities.Sessions.List) throw new NotSupportedException("The ACP agent does not advertise session listing.");
        if (workingDirectory is not null) ValidateWorkingDirectory(workingDirectory);
        return (await ListSessionsPageAsync(workingDirectory, cursor, cancellationToken).ConfigureAwait(false)).Sessions;
    }

    /// <inheritdoc />
    public async Task<AcpSessionListPage> ListSessionsPageAsync(
        string? workingDirectory = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!Agent.Capabilities.Sessions.List) throw new NotSupportedException("The ACP agent does not advertise session listing.");
        if (workingDirectory is not null) ValidateWorkingDirectory(workingDirectory);
        var response = await _connection.SendRequestAsync(
            "session/list",
            AcpWireJson.WriteListSessionsRequest(workingDirectory, cursor),
            cancellationToken).ConfigureAwait(false);
        return AcpWireJson.ReadSessionListPage(response);
    }

    /// <inheritdoc />
    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!Agent.Capabilities.Sessions.Delete) throw new NotSupportedException("The ACP agent does not advertise session deletion.");
        _ = await _connection.SendRequestAsync("session/delete", AcpWireJson.WriteSessionIdRequest(sessionId), cancellationToken).ConfigureAwait(false);
        _sessions.TryRemove(sessionId, out _);
    }

    /// <inheritdoc />
    public async Task<AcpSession> ResumeSessionAsync(string sessionId, AcpSessionOptions options, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(options);
        if (!Agent.Capabilities.Sessions.Resume) throw new NotSupportedException("The ACP agent does not advertise session resume.");
        ValidateWorkingDirectory(options.WorkingDirectory);
        ValidateAdditionalDirectories(options.AdditionalDirectories);
        ValidateMcpServers(options.McpServers, Agent.Capabilities.Mcp);
        if (options.AdditionalDirectories.Count > 0 && !Agent.Capabilities.Sessions.AdditionalDirectories)
            throw new NotSupportedException("The ACP agent does not advertise additionalDirectories support.");

        var created = new AcpSession(this, sessionId);
        var session = _sessions.GetOrAdd(sessionId, created);
        try
        {
            _ = await _connection.SendRequestAsync("session/resume", AcpWireJson.WriteResumeSessionRequest(sessionId, options), cancellationToken).ConfigureAwait(false);
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
        if (!Agent.Capabilities.Sessions.Close) throw new NotSupportedException("The ACP agent does not advertise session close.");
        _ = await _connection.SendRequestAsync("session/close", AcpWireJson.WriteSessionIdRequest(sessionId), cancellationToken).ConfigureAwait(false);
        _sessions.TryRemove(sessionId, out _);
    }

    internal async Task SetSessionModeAsync(string sessionId, string modeId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(modeId);
        _ = await _connection.SendRequestAsync("session/set_mode", AcpWireJson.WriteSetModeRequest(sessionId, modeId), cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<AcpConfigOption>> SetSessionConfigOptionAsync(string sessionId, string configId, AcpConfigValue value, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(configId);
        if ((value.ValueId is null) == (value.Boolean is null))
            throw new ArgumentException("Provide exactly one of a string value id or a boolean value.", nameof(value));
        var response = await _connection.SendRequestAsync("session/set_config_option", AcpWireJson.WriteSetConfigOptionRequest(sessionId, configId, value), cancellationToken).ConfigureAwait(false);
        return AcpWireJson.ReadConfigOptionsResponse(response);
    }

    internal async Task<string> PromptAsync(string sessionId, IReadOnlyList<AcpContentBlock> content, CancellationToken cancellationToken)
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
            var response = await _connection.SendRequestAsync("session/prompt", AcpWireJson.WritePromptRequest(sessionId, content), cancellationToken).ConfigureAwait(false);
            return AcpWireJson.ReadStopReason(response);
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
        await _connection.SendNotificationAsync("session/cancel", AcpWireJson.WriteCancelNotification(notification), cancellationToken).ConfigureAwait(false);
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

        var response = await _connection.SendRequestAsync("initialize", AcpWireJson.WriteInitializeRequest(request, _options.ClientInfo, _options), cancellationToken).ConfigureAwait(false);
        Agent = AcpWireJson.ReadAgentInfo(response);
        if (Agent.ProtocolVersion != ProtocolVersion)
        {
            throw new AcpProtocolException($"ACP protocol version {Agent.ProtocolVersion} was negotiated, but this client supports version {ProtocolVersion}.");
        }
    }

    private async ValueTask<byte[]> HandlePermissionRequestAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var request = AcpWireJson.ReadPermissionRequest(parameters);
        AcpPermissionDecision decision;
        var promptCancellation = _activePromptCancellation.TryGetValue(request.SessionId, out var activeCancellation)
            ? activeCancellation.Token
            : CancellationToken.None;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, promptCancellation);
        if (linkedCancellation.IsCancellationRequested)
        {
            decision = AcpPermissionDecision.Cancel();
        }
        else if (_options.PermissionHandler is not null)
        {
            try
            {
                decision = await _options.PermissionHandler.RequestPermissionAsync(request, linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
                decision = AcpPermissionDecision.Cancel();
            }
        }
        else
        {
            var rejectOption = request.Options.FirstOrDefault(option => option.Kind.StartsWith("reject", StringComparison.Ordinal));
            if (rejectOption is null)
            {
                throw new AcpRpcException(-32601, "No permission handler is configured and the agent supplied no reject option.", "session/request_permission");
            }
            decision = AcpPermissionDecision.Select(rejectOption.Id);
        }

        if (linkedCancellation.IsCancellationRequested) decision = AcpPermissionDecision.Cancel();
        if (!decision.Cancelled && (decision.SelectedOptionId is null || !request.Options.Any(option => option.Id == decision.SelectedOptionId)))
        {
            throw new AcpRpcException(-32602, "Permission handler returned an option id that was not offered by the agent.", "session/request_permission");
        }

        return AcpWireJson.WritePermissionResponse(decision);
    }

    private void RegisterHostCallbacks()
    {
        if (_options.FileSystemHandler is { } fileSystem)
        {
            _connection.RegisterRequestHandler("fs/read_text_file", async (parameters, cancellationToken) =>
            {
                var request = AcpWireJson.ReadTextFileReadRequest(parameters);
                ValidateAbsolutePath(request.Path, "path");
                var content = await fileSystem.ReadTextFileAsync(request.SessionId, request.Path, request.Line, request.Limit, cancellationToken).ConfigureAwait(false);
                return AcpWireJson.WriteTextFileReadResponse(content);
            });
            _connection.RegisterRequestHandler("fs/write_text_file", async (parameters, cancellationToken) =>
            {
                var request = AcpWireJson.ReadTextFileWriteRequest(parameters);
                ValidateAbsolutePath(request.Path, "path");
                await fileSystem.WriteTextFileAsync(request.SessionId, request.Path, request.Content, cancellationToken).ConfigureAwait(false);
                return AcpWireJson.WriteEmptyResponse();
            });
        }

        if (_options.TerminalHandler is { } terminal)
        {
            _connection.RegisterRequestHandler("terminal/create", async (parameters, cancellationToken) =>
            {
                var request = AcpWireJson.ReadTerminalCreateRequest(parameters);
                if (request.WorkingDirectory is not null) ValidateAbsolutePath(request.WorkingDirectory, "cwd");
                if (string.IsNullOrWhiteSpace(request.Command)) throw new AcpRpcException(-32602, "Terminal command must not be empty.", "terminal/create");
                var terminalId = await terminal.CreateAsync(request, cancellationToken).ConfigureAwait(false);
                return AcpWireJson.WriteTerminalCreateResponse(terminalId);
            });
            _connection.RegisterRequestHandler("terminal/output", async (parameters, cancellationToken) =>
            {
                var (sessionId, terminalId) = AcpWireJson.ReadTerminalRequest(parameters);
                var output = await terminal.GetOutputAsync(sessionId, terminalId, cancellationToken).ConfigureAwait(false);
                return AcpWireJson.WriteTerminalOutputResponse(output);
            });
            _connection.RegisterRequestHandler("terminal/wait_for_exit", async (parameters, cancellationToken) =>
            {
                var (sessionId, terminalId) = AcpWireJson.ReadTerminalRequest(parameters);
                var status = await terminal.WaitForExitAsync(sessionId, terminalId, cancellationToken).ConfigureAwait(false);
                return AcpWireJson.WriteTerminalExitStatusResponse(status);
            });
            _connection.RegisterRequestHandler("terminal/kill", async (parameters, cancellationToken) =>
            {
                var (sessionId, terminalId) = AcpWireJson.ReadTerminalRequest(parameters);
                await terminal.KillAsync(sessionId, terminalId, cancellationToken).ConfigureAwait(false);
                return AcpWireJson.WriteEmptyResponse();
            });
            _connection.RegisterRequestHandler("terminal/release", async (parameters, cancellationToken) =>
            {
                var (sessionId, terminalId) = AcpWireJson.ReadTerminalRequest(parameters);
                await terminal.ReleaseAsync(sessionId, terminalId, cancellationToken).ConfigureAwait(false);
                return AcpWireJson.WriteEmptyResponse();
            });
        }

        if (_options.ElicitationHandler is { } elicitation)
        {
            _connection.RegisterRequestHandler("elicitation/create", async (parameters, cancellationToken) =>
            {
                var request = AcpWireJson.ReadElicitationRequest(parameters);
                if (request.Mode == "form" && !elicitation.SupportsForm || request.Mode == "url" && !elicitation.SupportsUrl)
                {
                    throw new AcpRpcException(-32601, $"Elicitation mode '{request.Mode}' is not supported by this client.", "elicitation/create");
                }

                var response = await elicitation.RequestElicitationAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.Action is not ("accept" or "decline" or "cancel") && !response.Action.StartsWith('_'))
                {
                    throw new AcpRpcException(-32602, "Elicitation handler returned an unsupported action.", "elicitation/create");
                }
                return AcpWireJson.WriteElicitationResponse(response);
            });
        }

        _connection.RegisterNotificationHandler("elicitation/complete", parameters =>
        {
            _options.ElicitationCompleted?.Invoke(AcpWireJson.ReadElicitationCompleted(parameters));
            return ValueTask.CompletedTask;
        });
    }

    private ValueTask HandleSessionUpdateAsync(JsonElement parameters)
    {
        var sessionId = AcpWireJson.ReadNotificationSessionId(parameters);
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.Publish(AcpWireJson.ReadSessionUpdate(parameters));
        }
        return ValueTask.CompletedTask;
    }

    private void ValidatePromptContent(IReadOnlyList<AcpContentBlock> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        foreach (var block in content)
        {
            if (block is AcpImageContent && !Agent.Capabilities.PromptImage)
                throw new NotSupportedException("The ACP agent does not advertise image prompt support.");
            if (block is AcpAudioContent && !Agent.Capabilities.PromptAudio)
                throw new NotSupportedException("The ACP agent does not advertise audio prompt support.");
            if (block is AcpEmbeddedResource && !Agent.Capabilities.PromptEmbeddedContext)
                throw new NotSupportedException("The ACP agent does not advertise embedded context support.");
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

    private static void ValidateMcpServers(IReadOnlyList<AcpMcpServerOptions> servers, AcpMcpCapabilities capabilities)
    {
        foreach (var server in servers)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(server.Name);
            switch (server)
            {
                case AcpStdioMcpServerOptions stdio:
                    ArgumentException.ThrowIfNullOrWhiteSpace(stdio.Command);
                    if (!Path.IsPathFullyQualified(stdio.Command)) throw new ArgumentException("MCP stdio command must be an absolute path.", nameof(servers));
                    break;
                case AcpHttpMcpServerOptions http:
                    if (!Uri.TryCreate(http.Url, UriKind.Absolute, out _)) throw new ArgumentException("MCP HTTP URL must be absolute.", nameof(servers));
                    if (!capabilities.Http) throw new NotSupportedException("The ACP agent does not advertise HTTP MCP support.");
                    break;
                case AcpSseMcpServerOptions sse:
                    if (!Uri.TryCreate(sse.Url, UriKind.Absolute, out _)) throw new ArgumentException("MCP SSE URL must be absolute.", nameof(servers));
                    if (!capabilities.Sse) throw new NotSupportedException("The ACP agent does not advertise SSE MCP support.");
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
            throw new AcpRpcException(-32602, $"ACP {parameterName} must be an absolute path.");
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
