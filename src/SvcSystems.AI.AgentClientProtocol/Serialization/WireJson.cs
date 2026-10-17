using System.Buffers;
using System.Text.Json;
namespace SvcSystems.AI.AgentClientProtocol.Serialization;

internal static class WireJson
{
    public static Dictionary<string, JsonElement>? CopyMetadata(IReadOnlyDictionary<string, JsonElement>? metadata) =>
        metadata?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);

    public static byte[] WriteJsonValue(JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) value.WriteTo(writer);
        return buffer.WrittenSpan.ToArray();
    }

    public static AuthenticationStatus ReadAuthenticationStatus(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("authStatus", out var status) || status.ValueKind != JsonValueKind.Object)
            throw new ProtocolException("_auth/status_update must contain an authStatus object.");

        var kind = ReadString(status, "kind")
            ?? throw new ProtocolException("authStatus must contain a string kind.");
        var label = ReadString(status, "label")
            ?? throw new ProtocolException("authStatus must contain a string label.");
        AuthenticationAccount? account = null;
        if (status.TryGetProperty("account", out var accountValue) && accountValue.ValueKind != JsonValueKind.Null)
        {
            if (accountValue.ValueKind != JsonValueKind.Object)
                throw new ProtocolException("authStatus.account must be an object.");
            account = new AuthenticationAccount(
                ReadOptionalString(accountValue, "email"), ReadOptionalString(accountValue, "organization"), ReadOptionalString(accountValue, "plan"));
        }
        JsonElement? vendor = null;
        if (status.TryGetProperty("vendor", out var vendorValue) && vendorValue.ValueKind != JsonValueKind.Null)
        {
            if (vendorValue.ValueKind != JsonValueKind.Object)
                throw new ProtocolException("authStatus.vendor must be an object.");
            vendor = vendorValue.Clone();
        }
        return new AuthenticationStatus(kind, label, ReadOptionalString(status, "detail"), account, vendor, parameters.Clone());
    }

    private static string? ReadOptionalString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String)
            throw new ProtocolException($"Extension field '{name}' must be a string.");
        return property.GetString();
    }

    public static byte[] WriteInitializeRequest(InitializeRequest request, Implementation clientInfo, ClientOptions options)
    {
        var capabilities = request.ClientCapabilities;
        var session = options.SupportsCompactionUpdates || options.SupportsNoticeUpdates || options.SupportsBooleanSessionConfigOptions
            ? new ClientSessionCapabilities
            {
                Compaction = options.SupportsCompactionUpdates ? new CompactionCapabilities() : null,
                Notices = options.SupportsNoticeUpdates ? new NoticeCapabilities() : null,
                ConfigOptions = options.SupportsBooleanSessionConfigOptions
                    ? new SessionConfigOptionsCapabilities { Boolean = new BooleanConfigOptionCapabilities() }
                    : null
            }
            : null;
        var elicitation = options.ElicitationHandler is { } handler && (handler.SupportsForm || handler.SupportsUrl)
            ? new ElicitationCapabilities
            {
                Form = handler.SupportsForm ? new ElicitationFormCapabilities() : null,
                Url = handler.SupportsUrl ? new ElicitationUrlCapabilities() : null
            }
            : null;

        return JsonSerializer.SerializeToUtf8Bytes(new InitializeRequest
        {
            ProtocolVersion = request.ProtocolVersion,
            ClientCapabilities = new ClientCapabilities
            {
                Fs = capabilities?.Fs,
                Terminal = capabilities?.Terminal ?? false,
                Auth = capabilities?.Auth,
                Session = session,
                Elicitation = elicitation,
                Meta = capabilities?.Meta,
                ExtensionData = capabilities?.ExtensionData
            },
            ClientInfo = clientInfo,
            Meta = request.Meta,
            ExtensionData = request.ExtensionData
        }, JsonContext.Default.InitializeRequest);
    }

    public static byte[] WriteNewSessionRequest(NewSessionRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(new NewSessionRequest
        {
            Cwd = request.Cwd,
            McpServers = request.McpServers,
            AdditionalDirectories = request.AdditionalDirectories is { Count: > 0 } ? [.. request.AdditionalDirectories] : null,
            Meta = request.Meta,
            ExtensionData = request.ExtensionData
        }, JsonContext.Default.NewSessionRequest);

    public static byte[] WriteLoadSessionRequest(LoadSessionRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(new LoadSessionRequest
        {
            SessionId = request.SessionId,
            Cwd = request.Cwd,
            McpServers = request.McpServers,
            AdditionalDirectories = request.AdditionalDirectories is { Count: > 0 } ? [.. request.AdditionalDirectories] : null,
            Meta = request.Meta,
            ExtensionData = request.ExtensionData
        }, JsonContext.Default.LoadSessionRequest);

    public static byte[] WritePromptRequest(string sessionId, IReadOnlyList<ContentBlock> content, PromptOptions? options = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new PromptRequest
        {
            SessionId = sessionId,
            Prompt = content.ToList(),
            Meta = CopyMetadata(options?.Metadata)
        }, JsonContext.Default.PromptRequest);

    public static byte[] WriteResumeSessionRequest(string sessionId, SessionOptions options) =>
        JsonSerializer.SerializeToUtf8Bytes(new ResumeSessionRequest
        {
            SessionId = sessionId,
            Cwd = options.WorkingDirectory,
            McpServers = options.McpServers.Count > 0 ? [.. options.McpServers] : null,
            AdditionalDirectories = options.AdditionalDirectories.Count > 0 ? [.. options.AdditionalDirectories] : null,
            Meta = CopyMetadata(options.Metadata)
        }, JsonContext.Default.ResumeSessionRequest);

    public static byte[] WriteSetConfigOptionRequest(string sessionId, string configId, SetSessionConfigOptionRequest value)
    {
        // The allOf variants of the request schema only declare `value`/`type`; `sessionId` and
        // `configId` live on the enclosing request object, so they are re-attached as extension data.
        var common = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["sessionId"] = JsonSerializer.SerializeToElement(sessionId, JsonContext.Default.String),
            ["configId"] = JsonSerializer.SerializeToElement(configId, JsonContext.Default.String)
        };
        return value.Value switch
        {
            SetSessionConfigOptionRequestCase0 boolean => JsonSerializer.SerializeToUtf8Bytes(
                new SetSessionConfigOptionRequestCase0 { Value = boolean.Value, ExtensionData = common },
                JsonContext.Default.SetSessionConfigOptionRequestCase0),
            SetSessionConfigOptionRequestCase1 selection => JsonSerializer.SerializeToUtf8Bytes(
                new SetSessionConfigOptionRequestCase1 { Value = selection.Value, ExtensionData = common },
                JsonContext.Default.SetSessionConfigOptionRequestCase1),
            _ => throw new ArgumentException("A string or boolean session configuration value is required.", nameof(value))
        };
    }

    public static byte[] WritePermissionResponse(PermissionDecision decision) =>
        JsonSerializer.SerializeToUtf8Bytes(new RequestPermissionResponse
        {
            Outcome = decision.Cancelled
                ? new RequestPermissionOutcomeVariant0Cancelled()
                : new SelectedPermissionOutcome { OptionId = decision.SelectedOptionId! }
        }, JsonContext.Default.RequestPermissionResponse);

    public static string ReadNotificationSessionId(JsonElement parameters) =>
        ReadString(parameters, "sessionId") ?? throw new ProtocolException("ACP session notification did not include sessionId.");

    public static ElicitationRequest ReadElicitationRequest(JsonElement parameters)
    {
        var mode = ReadString(parameters, "mode") ?? "unknown";
        ElicitationSchema? requestedSchema = parameters.TryGetProperty("requestedSchema", out var schema) && schema.ValueKind != JsonValueKind.Null
            ? schema.Deserialize(JsonContext.Default.ElicitationSchema)!
            : null;
        return new ElicitationRequest(
            mode,
            ReadString(parameters, "message") ?? string.Empty,
            ReadString(parameters, "sessionId"),
            ReadString(parameters, "toolCallId"),
            ReadString(parameters, "requestId"),
            ReadString(parameters, "elicitationId"),
            ReadString(parameters, "url"),
            requestedSchema,
            parameters.Clone());
    }

    public static ElicitationCompleted ReadElicitationCompleted(JsonElement parameters) =>
        new(parameters.Deserialize(JsonContext.Default.CompleteElicitationNotification)!.ElicitationId, parameters.Clone());

    /// <summary>
    /// Serializes the empty JSON object that ACP returns for methods with no response payload
    /// (fs/write_text_file, terminal/kill, terminal/release).
    /// </summary>
    public static byte[] WriteEmptyObject() => "{}"u8.ToArray();

    public static byte[] WriteTerminalExitStatusResponse(TerminalExitStatus status) =>
        JsonSerializer.SerializeToUtf8Bytes(new TerminalExitStatus
        {
            ExitCode = status.ExitCode,
            Signal = status.Signal,
            Meta = status.Meta,
            ExtensionData = MergeNullOptionalValues(status.ExtensionData, status.ExitCode is null, status.Signal is null)
        }, JsonContext.Default.TerminalExitStatus);

    public static byte[] WriteElicitationResponse(ElicitationResponse response)
    {
        // The accept variant does not declare `action` (the schema leaves it to the union
        // discriminator), so it is re-attached as extension data.
        var extensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["action"] = JsonSerializer.SerializeToElement(response.Action, JsonContext.Default.String)
        };

        CreateElicitationResponse model = Discriminators.ParseCreateElicitationResponseDiscriminator(response.Action) switch
        {
            CreateElicitationResponseDiscriminator.Accept => new ElicitationAcceptAction
            {
                Content = response.Content?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                ExtensionData = extensionData
            },
            CreateElicitationResponseDiscriminator.Decline => new CreateElicitationResponseCase1(),
            CreateElicitationResponseDiscriminator.Cancel => new CreateElicitationResponseCase2(),
            _ => new CreateElicitationResponseCase3 { Action = response.Action }
        };
        return JsonSerializer.SerializeToUtf8Bytes(model, JsonContext.Default.CreateElicitationResponse);
    }

    public static SessionUpdate ReadSessionUpdate(JsonElement notification)
    {
        if (!notification.TryGetProperty("update", out var update) || update.ValueKind != JsonValueKind.Object)
        {
            throw new ProtocolException("session/update notification did not include an update object.");
        }

        return update.Deserialize(JsonContext.Default.SessionUpdate)!;
    }

    public static IReadOnlyList<string>? ReadPromptSuggestions(IReadOnlyDictionary<string, JsonElement>? meta)
    {
        if (meta is null || !meta.TryGetValue("promptSuggestions", out var suggestions)) return null;
        if (suggestions.ValueKind != JsonValueKind.Object ||
            !suggestions.TryGetProperty("suggestions", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new ProtocolException("promptSuggestions must contain a suggestions array.");
        return items.EnumerateArray().Select(item =>
            ReadString(item, "text") ?? throw new ProtocolException("Each prompt suggestion must contain a string text.")).ToArray();
    }

    public static IReadOnlyList<SessionConfigOption> ReadConfigOptionsResponse(JsonElement response) =>
        response.Deserialize(JsonContext.Default.SetSessionConfigOptionResponse)!.ConfigOptions;

    private static Dictionary<string, JsonElement>? MergeNullOptionalValues(Dictionary<string, JsonElement>? extensionData, bool exitCode, bool signal)
    {
        if (!exitCode && !signal) return extensionData;
        using var nullDocument = JsonDocument.Parse("null");
        var result = extensionData is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(extensionData, StringComparer.Ordinal);
        if (exitCode) result.TryAdd("exitCode", nullDocument.RootElement.Clone());
        if (signal) result.TryAdd("signal", nullDocument.RootElement.Clone());
        return result;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
