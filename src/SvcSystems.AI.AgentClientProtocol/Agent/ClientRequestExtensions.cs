using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SvcSystems.AI.AgentClientProtocol.Serialization;

namespace SvcSystems.AI.AgentClientProtocol.Agent;

/// <summary>
/// Typed helpers for the client methods an ACP agent can call, so handlers do not have to hand-build
/// JSON payloads for the file system, terminal, permission, and elicitation callbacks.
/// </summary>
/// <remarks>
/// Every helper serializes its request with the generated model metadata and deserializes the result
/// into the generated response model. Methods the protocol defines without a result payload return a
/// plain <see cref="Task"/>. Use <see cref="IAgentContext.SendClientRequestAsync"/> directly for
/// methods outside this set, for example ACP extensions.
/// </remarks>
public static class ClientRequestExtensions
{
    /// <summary>Reads a text file through the client (<c>fs/read_text_file</c>).</summary>
    /// <param name="context">The agent context for the active session.</param>
    /// <param name="request">The file to read.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>The client's response.</returns>
    public static Task<ReadTextFileResponse> ReadTextFileAsync(
        this IAgentContext context,
        ReadTextFileRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(context, Methods.FsReadTextFile, request, JsonContext.Default.ReadTextFileRequest, JsonContext.Default.ReadTextFileResponse, cancellationToken);

    /// <summary>Writes a text file through the client (<c>fs/write_text_file</c>).</summary>
    /// <param name="context">The agent context for the active session.</param>
    /// <param name="request">The file to write.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>A task that completes when the client has acknowledged the write.</returns>
    public static Task WriteTextFileAsync(
        this IAgentContext context,
        WriteTextFileRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(context, Methods.FsWriteTextFile, request, JsonContext.Default.WriteTextFileRequest, cancellationToken);

    /// <summary>Creates a terminal through the client (<c>terminal/create</c>).</summary>
    /// <param name="context">The agent context for the active session.</param>
    /// <param name="request">The command to run.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>The client's response, holding the new terminal identifier.</returns>
    public static Task<CreateTerminalResponse> CreateTerminalAsync(
        this IAgentContext context,
        CreateTerminalRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(context, Methods.TerminalCreate, request, JsonContext.Default.CreateTerminalRequest, JsonContext.Default.CreateTerminalResponse, cancellationToken);

    /// <summary>Reads the current output of a terminal through the client (<c>terminal/output</c>).</summary>
    /// <param name="context">The agent context for the active session.</param>
    /// <param name="request">The terminal to read.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>The client's response.</returns>
    public static Task<TerminalOutputResponse> GetTerminalOutputAsync(
        this IAgentContext context,
        TerminalOutputRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(context, Methods.TerminalOutput, request, JsonContext.Default.TerminalOutputRequest, JsonContext.Default.TerminalOutputResponse, cancellationToken);

    /// <summary>Waits for a terminal to exit through the client (<c>terminal/wait_for_exit</c>).</summary>
    /// <param name="context">The agent context for the active session.</param>
    /// <param name="request">The terminal to wait for.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>The client's response, holding the exit code or signal.</returns>
    public static Task<WaitForTerminalExitResponse> WaitForTerminalExitAsync(
        this IAgentContext context,
        WaitForTerminalExitRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(context, Methods.TerminalWaitForExit, request, JsonContext.Default.WaitForTerminalExitRequest, JsonContext.Default.WaitForTerminalExitResponse, cancellationToken);

    /// <summary>Kills a running terminal through the client (<c>terminal/kill</c>).</summary>
    /// <param name="context">The agent context for the active session.</param>
    /// <param name="request">The terminal to kill.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>A task that completes when the client has acknowledged the kill.</returns>
    public static Task KillTerminalAsync(
        this IAgentContext context,
        KillTerminalRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(context, Methods.TerminalKill, request, JsonContext.Default.KillTerminalRequest, cancellationToken);

    /// <summary>Releases a terminal through the client (<c>terminal/release</c>).</summary>
    /// <param name="context">The agent context for the active session.</param>
    /// <param name="request">The terminal to release.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>A task that completes when the client has acknowledged the release.</returns>
    public static Task ReleaseTerminalAsync(
        this IAgentContext context,
        ReleaseTerminalRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(context, Methods.TerminalRelease, request, JsonContext.Default.ReleaseTerminalRequest, cancellationToken);

    /// <summary>Asks the client for a permission decision (<c>session/request_permission</c>).</summary>
    /// <param name="context">The agent context for the active session.</param>
    /// <param name="request">The tool call to decide on and the options to offer.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>The client's response, holding the selected outcome.</returns>
    public static Task<RequestPermissionResponse> RequestPermissionAsync(
        this IAgentContext context,
        RequestPermissionRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(context, Methods.SessionRequestPermission, request, JsonContext.Default.RequestPermissionRequest, JsonContext.Default.RequestPermissionResponse, cancellationToken);

    /// <summary>Requests user input from the client (<c>elicitation/create</c>).</summary>
    /// <param name="context">The agent context for the active session.</param>
    /// <param name="request">The request, carrying a form, URL, or custom mode payload.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>The client's response, holding the action the user took.</returns>
    public static Task<CreateElicitationResponse> CreateElicitationAsync(
        this IAgentContext context,
        CreateElicitationRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync(context, Methods.ElicitationCreate, request, JsonContext.Default.CreateElicitationRequest, JsonContext.Default.CreateElicitationResponse, cancellationToken);

    /// <summary>Reports that an elicitation has been completed (<c>elicitation/complete</c>).</summary>
    /// <param name="context">The agent context for the active session.</param>
    /// <param name="notification">The elicitation that finished.</param>
    /// <param name="cancellationToken">A token that cancels the notification.</param>
    /// <returns>A task that completes when the notification has been written.</returns>
    public static ValueTask CompleteElicitationAsync(
        this IAgentContext context,
        CompleteElicitationNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(notification);
        var parameters = JsonSerializer.SerializeToElement(notification, JsonContext.Default.CompleteElicitationNotification);
        return context.SendClientNotificationAsync(Methods.ElicitationComplete, parameters, cancellationToken);
    }

    private static async Task<TResponse> SendAsync<TRequest, TResponse>(
        IAgentContext context,
        string method,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        var parameters = Serialize(context, method, request, requestTypeInfo);
        var result = await context.SendClientRequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        return result.Deserialize(responseTypeInfo)
            ?? throw new ProtocolException($"The client returned an empty result for '{method}'.");
    }

    private static async Task SendAsync<TRequest>(
        IAgentContext context,
        string method,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        CancellationToken cancellationToken)
    {
        var parameters = Serialize(context, method, request, requestTypeInfo);
        _ = await context.SendClientRequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
    }

    private static JsonElement Serialize<TRequest>(
        IAgentContext context,
        string method,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        return JsonSerializer.SerializeToElement(request, requestTypeInfo);
    }
}
