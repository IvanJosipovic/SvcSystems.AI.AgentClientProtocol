using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using SvcSystems.AI.AgentClientProtocol.Transport;

namespace SvcSystems.AI.AgentClientProtocol.JsonRpc;

internal sealed class JsonRpcConnection : IAsyncDisposable
{
    private readonly ITransport _transport;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, ValueTask<byte[]>>> _requestHandlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<JsonElement, ValueTask>> _notificationHandlers = new(StringComparer.Ordinal);
    private readonly Task _receiveTask;
    private readonly TaskCompletionSource<Exception?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _terminalException;
    private long _nextRequestId;
    private int _disposed;

    public JsonRpcConnection(ITransport transport)
    {
        _transport = transport;
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

    public void RegisterNotificationHandler(string method, Func<JsonElement, ValueTask> handler)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _notificationHandlers.Add(method, handler);
    }

    public async Task<JsonElement> SendRequestAsync(
        string method,
        ReadOnlyMemory<byte> parameters,
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        cancellationToken.ThrowIfCancellationRequested();

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException("Failed to register a JSON-RPC request id.");
        }

        using var registration = cancellationToken.Register(static state =>
        {
            var pending = (PendingCancellation)state!;
            if (pending.Connection._pendingRequests.TryRemove(pending.RequestId, out var source))
            {
                source.TrySetCanceled(pending.Token);
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

    public ValueTask SendNotificationAsync(string method, ReadOnlyMemory<byte> parameters, CancellationToken cancellationToken) =>
        _transport.SendAsync(WriteNotification(method, parameters), cancellationToken);

    private async Task SendCancellationAsync(long requestId)
    {
        try
        {
            var parameters = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(parameters))
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", requestId);
                writer.WriteEndObject();
            }
            await SendNotificationAsync("$/cancel_request", parameters.WrittenMemory, CancellationToken.None).ConfigureAwait(false);
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

            if (!cancellationToken.IsCancellationRequested)
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
                    _ = DispatchRequestAsync(method, id.Clone(), parameters);
                }
                else if (_notificationHandlers.TryGetValue(method, out var handler))
                {
                    await DispatchNotificationAsync(handler, parameters).ConfigureAwait(false);
                }
                else
                {
                    throw new ProtocolException($"Notification '{method}' is not supported.");
                }
                return;
            }

            if (!message.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt64(out var idValue))
            {
                throw new ProtocolException("ACP JSON-RPC response must include an integer request id.");
            }

            if (!_pendingRequests.TryRemove(idValue, out var pending))
            {
                return;
            }

            if (message.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var codeValue) && codeValue.TryGetInt32(out var parsedCode) ? parsedCode : -32603;
                var errorMessage = error.TryGetProperty("message", out var messageValue) ? messageValue.GetString() ?? "Unknown JSON-RPC error." : "Unknown JSON-RPC error.";
                JsonElement? data = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("data", out var dataValue)
                    ? dataValue.Clone()
                    : null;
                pending.TrySetException(new RpcException(code, errorMessage, data: data));
            }
            else if (message.TryGetProperty("result", out var result))
            {
                pending.TrySetResult(result.Clone());
            }
            else
            {
                pending.TrySetException(new ProtocolException("JSON-RPC response must contain result or error."));
            }
        }
    }

    private async Task DispatchRequestAsync(string method, JsonElement id, JsonElement parameters)
    {
        try
        {
            if (!_requestHandlers.TryGetValue(method, out var handler))
            {
                await SendIncomingErrorAsync(id, -32601, $"Method '{method}' is not supported.").ConfigureAwait(false);
                return;
            }

            var result = await handler(parameters, _shutdown.Token).ConfigureAwait(false);
            await SendIncomingResultAsync(id, result).ConfigureAwait(false);
        }
        catch (RpcException exception)
        {
            await SendIncomingErrorAsync(id, exception.Code, exception.Message, exception.ErrorData).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            await SendIncomingErrorAsync(id, -32800, "Request cancelled.").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await SendIncomingErrorAsync(id, -32603, exception.Message).ConfigureAwait(false);
        }
    }

    private static Task DispatchNotificationAsync(Func<JsonElement, ValueTask> handler, JsonElement parameters) =>
        handler(parameters).AsTask();

    private ValueTask SendIncomingResultAsync(JsonElement id, ReadOnlyMemory<byte> result) =>
        _transport.SendAsync(WriteResponse(id, result), _shutdown.Token);

    private ValueTask SendIncomingErrorAsync(JsonElement id, int code, string message, JsonElement? data = null) =>
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

    private static byte[] WriteResponse(JsonElement id, ReadOnlyMemory<byte> result) =>
        WriteEnvelope(writer =>
        {
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            writer.WritePropertyName("result");
            writer.WriteRawValue(result.Span, skipInputValidation: true);
        });

    private static byte[] WriteError(JsonElement id, int code, string message, JsonElement? data) =>
        WriteEnvelope(writer =>
        {
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            writer.WriteStartObject("error");
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            if (data is { } errorData)
            {
                writer.WritePropertyName("data");
                errorData.WriteTo(writer);
            }
            writer.WriteEndObject();
        });

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
            if (_pendingRequests.TryRemove(item.Key, out var completion)) completion.TrySetException(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        FailPending(new ObjectDisposedException(nameof(JsonRpcConnection)));
        await _transport.DisposeAsync().ConfigureAwait(false);
        try { await _receiveTask.ConfigureAwait(false); }
        catch { /* receive loop records and distributes terminal exceptions itself */ }
        _shutdown.Dispose();
    }

    private sealed record PendingCancellation(JsonRpcConnection Connection, long RequestId, CancellationToken Token);
}
