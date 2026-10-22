using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SvcSystems.AI.AgentClientProtocol.Serialization;
using SvcSystems.AI.AgentClientProtocol.Transport;

namespace SvcSystems.AI.AgentClientProtocol.JsonRpc;

internal sealed class JsonRpcConnection : IAsyncDisposable
{
    private readonly ITransport _transport;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _incomingRequestSlots;
    private readonly Action<string, Exception>? _requestHandlerException;
    private readonly ILogger? _logger;
    private readonly bool _treatTransportEndAsClean;
    private readonly object _incomingRequestsLock = new();
    private readonly HashSet<Task> _incomingRequests = [];
    private readonly ConcurrentDictionary<long, PendingRequest> _pendingRequests = new();
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, ValueTask<byte[]>>> _requestHandlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<JsonElement, ValueTask>> _notificationHandlers = new(StringComparer.Ordinal);
    private readonly Task _receiveTask;
    private readonly TaskCompletionSource<Exception?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _terminalException;
    private long _nextRequestId;
    private int _disposed;

    public Action<string, JsonElement>? UnhandledNotificationReceived { get; set; }

    public JsonRpcConnection(
        ITransport transport,
        int maximumConcurrentIncomingRequests = 32,
        Action<string, Exception>? requestHandlerException = null,
        ILoggerFactory? loggerFactory = null,
        bool treatTransportEndAsClean = false)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (maximumConcurrentIncomingRequests <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentIncomingRequests));
        }
        _transport = transport;
        _incomingRequestSlots = new SemaphoreSlim(maximumConcurrentIncomingRequests);
        _requestHandlerException = requestHandlerException;
        _logger = loggerFactory?.CreateLogger<JsonRpcConnection>();
        _treatTransportEndAsClean = treatTransportEndAsClean;
        _receiveTask = ReceiveLoopAsync(_shutdown.Token);
    }

    /// <summary>Completes when the transport closes. The result is the failure, or null for a clean close.</summary>
    public Task<Exception?> Completion => _completion.Task;

    public void RegisterRequestHandler(
        string method,
        Func<JsonElement, CancellationToken, ValueTask<byte[]>> handler)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _requestHandlers.Add(method, handler);
    }

    /// <summary>Registers a handler for agent-to-client notifications.</summary>
    /// <remarks>
    /// Handlers are awaited inline on the receive loop so that notifications reach them in wire order
    /// and the loop keeps transport reads bounded. A handler must therefore not block: in particular it
    /// must not await a request or notification send on this connection, because a peer that has
    /// stopped reading its stdin would stall response processing for the whole connection (and can
    /// deadlock stdio). Work that needs to write to the transport must be detached from the dispatch
    /// path.
    /// </remarks>
    public void RegisterNotificationHandler(string method, Func<JsonElement, ValueTask> handler)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _notificationHandlers.Add(method, handler);
    }

    /// <summary>Sends a request and awaits its response.</summary>
    /// <param name="method">The JSON-RPC method name.</param>
    /// <param name="parameters">The serialized request parameters.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <param name="responseObserver">
    /// An optional observer for the response. It runs on the receive loop before the awaiting
    /// caller continues, so a caller can register state that a notification sent immediately
    /// after the response depends on. An observer that throws is reported and ignored.
    /// </param>
    /// <returns>The response result.</returns>
    public async Task<JsonElement> SendRequestAsync(
        string method,
        ReadOnlyMemory<byte> parameters,
        CancellationToken cancellationToken,
        Action<JsonElement>? responseObserver = null)
    {
        ThrowIfUnavailable();
        cancellationToken.ThrowIfCancellationRequested();

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest(completion, responseObserver);
        if (!_pendingRequests.TryAdd(requestId, pending))
        {
            throw new InvalidOperationException("Failed to register a JSON-RPC request id.");
        }

        using var registration = cancellationToken.Register(static state =>
        {
            var pending = (PendingCancellation)state!;
            if (pending.Connection._pendingRequests.TryRemove(pending.RequestId, out var source))
            {
                source.Completion.TrySetCanceled(pending.Token);
                _ = pending.Connection.SendCancellationAsync(pending.RequestId);
            }
        }, new PendingCancellation(this, requestId, cancellationToken));

        try
        {
            await _transport.SendAsync(WriteRequest(requestId, method, parameters), cancellationToken).ConfigureAwait(false);
            return await completion.Task.ConfigureAwait(false);
        }
        catch
        {
            _pendingRequests.TryRemove(requestId, out _);
            throw;
        }
    }

    public ValueTask SendNotificationAsync(string method, ReadOnlyMemory<byte> parameters, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        cancellationToken.ThrowIfCancellationRequested();
        return _transport.SendAsync(WriteNotification(method, parameters), cancellationToken);
    }

    private async Task SendCancellationAsync(long requestId)
    {
        try
        {
            // The ACP schema names the cancelled request "requestId"; a peer that requires that
            // property silently ignores a notification that sends "id" instead.
            var parameters = JsonSerializer.SerializeToUtf8Bytes(
                new CancelRequestNotification { RequestId = requestId },
                JsonContext.Default.CancelRequestNotification);
            await SendNotificationAsync(Methods.CancelRequest, parameters, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The original cancellation remains primary; transport failure faults remaining requests via the read loop.
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            {
                await ProcessMessageAsync(frame).ConfigureAwait(false);
            }

            if (!cancellationToken.IsCancellationRequested && !_treatTransportEndAsClean)
            {
                throw new ProcessException("The ACP agent closed stdout while the connection was active.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _terminalException = exception;
            _logger?.LogDebug(exception, "The ACP connection terminated with a transport or protocol failure.");
            FailPending(exception);
        }
        finally
        {
            _completion.TrySetResult(_terminalException);
        }
    }

    private async Task ProcessMessageAsync(ReadOnlyMemory<byte> frame)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(frame);
        }
        catch (JsonException exception)
        {
            throw new ProtocolException("ACP agent stdout contained invalid JSON.", exception);
        }

        using (document)
        {
            var message = document.RootElement;
            if (message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("jsonrpc", out var jsonRpc) ||
                jsonRpc.GetString() != "2.0")
            {
                throw new ProtocolException("ACP message must be a JSON object with jsonrpc = '2.0'.");
            }

            if (message.TryGetProperty("method", out var methodValue))
            {
                var method = methodValue.GetString();
                if (string.IsNullOrWhiteSpace(method)) throw new ProtocolException("ACP method name must be a non-empty string.");
                var parameters = GetParameters(message);
                if (message.TryGetProperty("id", out var id))
                {
                    var requestId = ReadRequestId(id);
                    if (!_incomingRequestSlots.Wait(0))
                    {
                        await SendIncomingErrorAsync(requestId, -32000, "Too many concurrent requests.").ConfigureAwait(false);
                        return;
                    }

                    TrackIncomingRequest(DispatchTrackedRequestAsync(method, requestId, parameters));
                }
                else if (_notificationHandlers.TryGetValue(method, out var handler))
                {
                    // Awaited inline on purpose: notifications must reach handlers in wire order, and the
                    // loop supplies the backpressure that keeps the transport buffers bounded. See
                    // RegisterNotificationHandler for the handler contract this implies.
                    await DispatchNotificationAsync(method, handler, parameters).ConfigureAwait(false);
                }
                else
                {
                    UnhandledNotificationReceived?.Invoke(method, parameters);
                }
                return;
            }

            if (!message.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number)
            {
                throw new ProtocolException("ACP JSON-RPC response must include an integer request id.");
            }
            var responseRequestId = ReadRequestId(responseId);
            if (responseRequestId is not long idValue)
                throw new ProtocolException("ACP JSON-RPC response must include an integer request id.");

            if (!_pendingRequests.TryRemove(idValue, out var pending))
            {
                return;
            }

            if (message.TryGetProperty("error", out var error))
            {
                try
                {
                    var rpcError = error.Deserialize(JsonContext.Default.Error)!;
                    pending.Completion.TrySetException(new RpcException(rpcError.Code, rpcError.Message, data: rpcError.Data));
                }
                catch (JsonException exception)
                {
                    pending.Completion.TrySetException(new ProtocolException("JSON-RPC error response is malformed.", exception));
                }
            }
            else if (message.TryGetProperty("result", out var result))
            {
                // The observer runs on the receive loop before the awaiting caller continues, so a
                // caller can register state that an immediately following notification depends on.
                var value = result.Clone();
                try
                {
                    pending.ResponseObserver?.Invoke(value);
                }
                catch (Exception exception)
                {
                    _logger?.LogWarning(exception, "A JSON-RPC response observer failed.");
                    Trace.TraceError("A JSON-RPC response observer failed: {0}", exception);
                }
                pending.Completion.TrySetResult(value);
            }
            else
            {
                pending.Completion.TrySetException(new ProtocolException("JSON-RPC response must contain result or error."));
            }
        }
    }

    private async Task DispatchRequestAsync(string method, RequestId? id, JsonElement parameters)
    {
        if (!_requestHandlers.TryGetValue(method, out var handler))
        {
            await SendIncomingErrorAsync(id, -32601, $"Method '{method}' is not supported.").ConfigureAwait(false);
            return;
        }

        byte[] result;
        try
        {
            result = await handler(parameters, _shutdown.Token).ConfigureAwait(false);
        }
        catch (RpcException exception)
        {
            await SendIncomingErrorAsync(id, exception.Code, exception.Message, exception.ErrorData).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // The connection is shutting down, so there is nobody left to answer. The shutdown
            // itself is reported through Completion; this request is simply abandoned.
            return;
        }
        catch (Exception exception)
        {
            ReportRequestHandlerException(method, exception);
            await SendIncomingErrorAsync(id, -32603, "Internal error while handling the request.").ConfigureAwait(false);
            return;
        }

        await SendIncomingResultAsync(id, result).ConfigureAwait(false);
    }

    private async Task DispatchTrackedRequestAsync(string method, RequestId? id, JsonElement parameters)
    {
        try
        {
            await DispatchRequestAsync(method, id, parameters).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (!_shutdown.IsCancellationRequested)
            {
                // A failure while the connection is live is terminal. A failure caused by the
                // shutdown itself is not: aborting a request because the connection is closing
                // must not be reported to the peer as a protocol failure.
                Interlocked.CompareExchange(ref _terminalException, exception, null);
                FailPending(_terminalException!);
            }
            _shutdown.Cancel();
        }
        finally
        {
            _incomingRequestSlots.Release();
        }
    }

    private void TrackIncomingRequest(Task task)
    {
        lock (_incomingRequestsLock) _incomingRequests.Add(task);
        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var connection = (JsonRpcConnection)state!;
                lock (connection._incomingRequestsLock) connection._incomingRequests.Remove(completed);
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DrainIncomingRequestsAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_incomingRequestsLock)
            {
                if (_incomingRequests.Count == 0) return;
                tasks = [.. _incomingRequests];
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private void ReportRequestHandlerException(string method, Exception exception)
    {
        try
        {
            if (_requestHandlerException is not null)
            {
                _requestHandlerException(method, exception);
            }
            else if (_logger is not null)
            {
                _logger.LogWarning(exception, "Incoming JSON-RPC request handler for {Method} failed.", method);
            }
            else
            {
                Trace.TraceError("Incoming JSON-RPC request handler for '{0}' failed: {1}", method, exception);
            }
        }
        catch (Exception diagnosticException)
        {
            Trace.TraceError("The request-handler diagnostic callback failed: {0}", diagnosticException);
        }
    }

    private static Task DispatchNotificationAsync(Func<JsonElement, ValueTask> handler, JsonElement parameters) =>
        handler(parameters).AsTask();

    /// <summary>
    /// Dispatches a notification, isolating malformed payloads from the receive loop.
    /// </summary>
    /// <remarks>
    /// A notification whose payload cannot be interpreted is reported and skipped so that one bad
    /// frame does not tear down an otherwise healthy connection. Any other exception comes from an
    /// application callback and keeps its documented contract: it terminates the connection and is
    /// surfaced through <see cref="Completion"/>.
    /// </remarks>
    private async Task DispatchNotificationAsync(string method, Func<JsonElement, ValueTask> handler, JsonElement parameters)
    {
        try
        {
            await handler(parameters).ConfigureAwait(false);
        }
        catch (ProtocolException exception)
        {
            _logger?.LogWarning(exception, "An ACP notification for {Method} was rejected and skipped.", method);
            Trace.TraceError("An ACP notification for '{0}' was rejected and skipped: {1}", method, exception);
        }
    }

    private ValueTask SendIncomingResultAsync(RequestId? id, ReadOnlyMemory<byte> result) =>
        _transport.SendAsync(WriteResponse(id, result), _shutdown.Token);

    private ValueTask SendIncomingErrorAsync(RequestId? id, int code, string message, JsonElement? data = null) =>
        _transport.SendAsync(WriteError(id, code, message, data), _shutdown.Token);

    private static JsonElement GetParameters(JsonElement message)
    {
        if (message.TryGetProperty("params", out var parameters)) return parameters.Clone();
        using var empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }

    private static byte[] WriteRequest(long requestId, string method, ReadOnlyMemory<byte> parameters) =>
        WriteEnvelope(writer =>
        {
            writer.WriteNumber("id", requestId);
            writer.WriteString("method", method);
            WriteParameters(writer, parameters);
        });

    private static byte[] WriteNotification(string method, ReadOnlyMemory<byte> parameters) =>
        WriteEnvelope(writer =>
        {
            writer.WriteString("method", method);
            WriteParameters(writer, parameters);
        });

    private static byte[] WriteResponse(RequestId? id, ReadOnlyMemory<byte> result) =>
        WriteEnvelope(writer =>
        {
            writer.WritePropertyName("id");
            WriteRequestId(writer, id);
            writer.WritePropertyName("result");
            writer.WriteRawValue(result.Span, skipInputValidation: true);
        });

    private static byte[] WriteError(RequestId? id, int code, string message, JsonElement? data) =>
        WriteEnvelope(writer =>
        {
            writer.WritePropertyName("id");
            WriteRequestId(writer, id);
            writer.WritePropertyName("error");
            writer.WriteRawValue(JsonSerializer.SerializeToUtf8Bytes(new Error { Code = code, Message = message, Data = data }, JsonContext.Default.Error), skipInputValidation: true);
        });

    private static RequestId? ReadRequestId(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        try
        {
            return value.Deserialize(JsonContext.Default.RequestId)!;
        }
        catch (JsonException exception)
        {
            throw new ProtocolException("JSON-RPC request id must be a string, integer, or null.", exception);
        }
    }

    private static void WriteRequestId(Utf8JsonWriter writer, RequestId? requestId)
    {
        if (requestId is null)
        {
            writer.WriteNullValue();
            return;
        }

        switch (requestId)
        {
            case long numericId:
                writer.WriteNumberValue(numericId);
                break;
            case string stringId:
                writer.WriteStringValue(stringId);
                break;
            default:
                throw new ProtocolException("JSON-RPC request id used an unsupported schema variant.");
        }
    }

    private static void WriteParameters(Utf8JsonWriter writer, ReadOnlyMemory<byte> parameters)
    {
        using var parametersDocument = JsonDocument.Parse(parameters);
        writer.WritePropertyName("params");
        parametersDocument.RootElement.WriteTo(writer);
    }

    private static byte[] WriteEnvelope(Action<Utf8JsonWriter> writeBody)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writeBody(writer);
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_terminalException is { } exception) throw new ProcessException("The ACP connection has terminated.", exception);
    }

    private void FailPending(Exception exception)
    {
        foreach (var item in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(item.Key, out var completion)) completion.Completion.TrySetException(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // A deliberate disposal is a clean close, not a failure. The completion is resolved before
        // the receive loop is torn down so that a caller awaiting it (for example an agent host
        // running the connection) observes a clean result instead of racing the shutdown token
        // and seeing a cancellation.
        _completion.TrySetResult(_terminalException);
        _shutdown.Cancel();
        FailPending(new ObjectDisposedException(nameof(JsonRpcConnection)));
        await _transport.DisposeAsync().ConfigureAwait(false);
        try { await _receiveTask.ConfigureAwait(false); }
        catch { /* receive loop records and distributes terminal exceptions itself */ }
        await DrainIncomingRequestsAsync().ConfigureAwait(false);
        _incomingRequestSlots.Dispose();
        _shutdown.Dispose();
    }

    private sealed record PendingCancellation(JsonRpcConnection Connection, long RequestId, CancellationToken Token);

    /// <summary>A request awaiting its response, plus an optional observer that runs on the receive loop.</summary>
    private sealed class PendingRequest(
        TaskCompletionSource<JsonElement> completion,
        Action<JsonElement>? responseObserver)
    {
        public TaskCompletionSource<JsonElement> Completion { get; } = completion;

        public Action<JsonElement>? ResponseObserver { get; } = responseObserver;
    }
}
