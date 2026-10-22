using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SvcSystems.AI.AgentClientProtocol.JsonRpc;
using SvcSystems.AI.AgentClientProtocol.Serialization;
using SvcSystems.AI.AgentClientProtocol.Transport;

namespace SvcSystems.AI.AgentClientProtocol.Agent;

/// <summary>Hosts an ACP agent over a caller-provided transport or the process stdio streams.</summary>
public sealed class Agent : IAsyncDisposable
{
    /// <summary>The ACP protocol version this host speaks.</summary>
    private const int ProtocolVersion = 1;

    private readonly IAgentHandler _handler;
    private readonly DeferredTransport _transport;
    private readonly JsonRpcConnection _connection;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activePrompts = new(StringComparer.Ordinal);
    private int _started;
    private int _disposed;

    /// <summary>Creates an ACP agent host. If no transport is supplied, stdin/stdout stdio is used.</summary>
    public Agent(IAgentHandler handler, ITransport? transport = null, AgentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new AgentOptions();
        if (options.MaximumConcurrentIncomingRequests <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum concurrent incoming requests must be positive.");
        }
        if (options.MaximumMessageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum message bytes must be positive.");
        }

        _handler = handler;
        _transport = new DeferredTransport(transport ?? new StdioHostTransport(options.MaximumMessageBytes));
        _connection = new JsonRpcConnection(
            _transport,
            options.MaximumConcurrentIncomingRequests,
            treatTransportEndAsClean: true);
        RegisterHandlers();
    }

    /// <summary>Completes when the connected client disconnects or the agent host is stopped.</summary>
    public Task<Exception?> Completion => _connection.Completion;

    /// <summary>Runs until the client disconnects, the host is stopped, or the cancellation token is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("An ACP agent host can only be run once.");
        }

        _transport.Start();
        try
        {
            var failure = await _connection.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                throw failure;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's token cancelled the run. A host that was stopped deliberately is not
            // a failure, so the cancellation is swallowed and the run ends cleanly.
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Stops the host and cancels active prompt handlers.</summary>
    public ValueTask StopAsync() => DisposeAsync();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _transport.Start();
        foreach (var source in _activePrompts.Values)
        {
            try
            {
                await source.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The prompt completed between snapshotting the active prompts and cancelling it.
            }
            catch (Exception exception)
            {
                Trace.TraceError("ACP agent prompt cancellation callback failed: {0}", exception);
            }
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
        foreach (var source in _activePrompts.Values)
        {
            source.Dispose();
        }
        _activePrompts.Clear();
        GC.SuppressFinalize(this);
    }

    private void RegisterHandlers()
    {
        _connection.RegisterRequestHandler(Methods.Initialize, async (parameters, cancellationToken) =>
        {
            var request = parameters.Deserialize(JsonContext.Default.InitializeRequest)
                ?? throw new RpcException(-32602, "initialize params are required.", Methods.Initialize);
            var response = await _handler.InitializeAsync(request, cancellationToken).ConfigureAwait(false);
            // ACP requires the agent to answer with "the protocol version the client specified if
            // supported by the agent, or the latest protocol version supported by the agent".
            // Echoing an unsupported version verbatim is not conformant, so the host reports the
            // version it actually speaks. The client disconnects when it cannot speak that version.
            var negotiated = response.ProtocolVersion == ProtocolVersion
                ? response
                : new InitializeResponse
                {
                    ProtocolVersion = ProtocolVersion,
                    AgentCapabilities = response.AgentCapabilities,
                    AuthMethods = response.AuthMethods,
                    AgentInfo = response.AgentInfo,
                    Meta = response.Meta,
                    ExtensionData = response.ExtensionData,
                };
            return JsonSerializer.SerializeToUtf8Bytes(negotiated, JsonContext.Default.InitializeResponse);
        });

        _connection.RegisterRequestHandler(Methods.SessionNew, async (parameters, cancellationToken) =>
        {
            var request = parameters.Deserialize(JsonContext.Default.NewSessionRequest)
                ?? throw new RpcException(-32602, "session/new params are required.", Methods.SessionNew);
            var response = await _handler.NewSessionAsync(request, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.SerializeToUtf8Bytes(response, JsonContext.Default.NewSessionResponse);
        });

        _connection.RegisterRequestHandler(Methods.SessionPrompt, HandlePromptAsync);
        RegisterRequestHandler(Methods.SessionLoad, JsonContext.Default.LoadSessionRequest, JsonContext.Default.LoadSessionResponse, _handler.LoadSessionAsync);
        RegisterRequestHandler(Methods.SessionResume, JsonContext.Default.ResumeSessionRequest, JsonContext.Default.ResumeSessionResponse, _handler.ResumeSessionAsync);
        RegisterRequestHandler(Methods.SessionList, JsonContext.Default.ListSessionsRequest, JsonContext.Default.ListSessionsResponse, _handler.ListSessionsAsync);
        RegisterRequestHandler(Methods.SessionDelete, JsonContext.Default.DeleteSessionRequest, JsonContext.Default.DeleteSessionResponse, _handler.DeleteSessionAsync);
        RegisterRequestHandler(Methods.SessionClose, JsonContext.Default.CloseSessionRequest, JsonContext.Default.CloseSessionResponse, _handler.CloseSessionAsync);
        RegisterRequestHandler(Methods.SessionSetMode, JsonContext.Default.SetSessionModeRequest, JsonContext.Default.SetSessionModeResponse, _handler.SetSessionModeAsync);
        RegisterRequestHandler(
            Methods.SessionSetConfigOption,
            JsonContext.Default.SetSessionConfigOptionRequest,
            JsonContext.Default.SetSessionConfigOptionResponse,
            _handler.SetSessionConfigOptionAsync);
        RegisterRequestHandler(Methods.Authenticate, JsonContext.Default.AuthenticateRequest, JsonContext.Default.AuthenticateResponse, _handler.AuthenticateAsync);
        RegisterRequestHandler(Methods.Logout, JsonContext.Default.LogoutRequest, JsonContext.Default.LogoutResponse, _handler.LogoutAsync);
        _connection.RegisterNotificationHandler(Methods.SessionCancel, HandleCancelAsync);
    }

    private void RegisterRequestHandler<TRequest, TResponse>(
        string method,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        Func<TRequest, CancellationToken, ValueTask<TResponse>> handler)
    {
        _connection.RegisterRequestHandler(method, async (parameters, cancellationToken) =>
        {
            TRequest request;
            try
            {
                var deserialized = parameters.Deserialize(requestTypeInfo);
                if (deserialized is null)
                {
                    throw new JsonException("Request params cannot be null.");
                }
                request = deserialized;
            }
            catch (JsonException)
            {
                throw new RpcException(-32602, "Request params are invalid.", method);
            }

            var response = await handler(request, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.SerializeToUtf8Bytes(response, responseTypeInfo);
        });
    }

    private async ValueTask<byte[]> HandlePromptAsync(JsonElement parameters, CancellationToken shutdownToken)
    {
        var request = parameters.Deserialize(JsonContext.Default.PromptRequest)
            ?? throw new RpcException(-32602, "session/prompt params are required.", "session/prompt");
        using var promptCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        if (!_activePrompts.TryAdd(request.SessionId, promptCancellation))
        {
            throw new RpcException(-32602, $"A prompt is already active for session '{request.SessionId}'.", "session/prompt");
        }

        try
        {
            var context = new AgentContext(_connection);
            PromptResponse response;
            try
            {
                response = await _handler.PromptAsync(request, context, promptCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (promptCancellation.IsCancellationRequested)
            {
                // A prompt cancelled by the host shutting down is not a protocol failure either.
                // The turn ends as "cancelled" so the peer sees a well-formed response.
                response = new PromptResponse { StopReason = "cancelled" };
            }

            return JsonSerializer.SerializeToUtf8Bytes(response, JsonContext.Default.PromptResponse);
        }
        finally
        {
            _activePrompts.TryRemove(request.SessionId, out _);
        }
    }

    private ValueTask HandleCancelAsync(JsonElement parameters)
    {
        var notification = parameters.Deserialize(JsonContext.Default.CancelNotification)
            ?? throw new RpcException(-32602, "session/cancel params are required.", "session/cancel");
        if (_activePrompts.TryGetValue(notification.SessionId, out var promptCancellation))
        {
            _ = CancelPromptWithoutBlockingAsync(promptCancellation);
        }

        return ValueTask.CompletedTask;
    }

    private static async Task CancelPromptWithoutBlockingAsync(CancellationTokenSource promptCancellation)
    {
        try
        {
            await promptCancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The prompt completed after the cancellation notification looked it up.
        }
        catch (Exception exception)
        {
            Trace.TraceError("ACP agent prompt cancellation callback failed: {0}", exception);
        }
    }
}
