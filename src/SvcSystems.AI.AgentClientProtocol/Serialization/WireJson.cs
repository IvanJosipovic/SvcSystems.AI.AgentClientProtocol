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
                Meta = capabilities?.Meta
            },
            ClientInfo = clientInfo,
            Meta = request.Meta
        }, JsonContext.Default.InitializeRequest);
    }

    public static byte[] WriteNewSessionRequest(NewSessionRequest request)
    {
        List<string>? additionalDirectories = request.AdditionalDirectories is { Count: > 0 } ? [.. request.AdditionalDirectories] : null;
        var wireRequest = additionalDirectories is null
            ? new NewSessionRequest
            {
                Cwd = request.Cwd,
                McpServers = request.McpServers,
                Meta = request.Meta
            }
            : new NewSessionRequest
            {
                Cwd = request.Cwd,
                McpServers = request.McpServers,
                AdditionalDirectories = additionalDirectories,
                Meta = request.Meta
            };
        return JsonSerializer.SerializeToUtf8Bytes(wireRequest, JsonContext.Default.NewSessionRequest);
    }

    public static byte[] WriteLoadSessionRequest(LoadSessionRequest request)
    {
        List<string>? additionalDirectories = request.AdditionalDirectories is { Count: > 0 } ? [.. request.AdditionalDirectories] : null;
        var wireRequest = additionalDirectories is null
            ? new LoadSessionRequest
            {
                SessionId = request.SessionId,
                Cwd = request.Cwd,
                McpServers = request.McpServers,
                Meta = request.Meta
            }
            : new LoadSessionRequest
            {
                SessionId = request.SessionId,
                Cwd = request.Cwd,
                McpServers = request.McpServers,
                AdditionalDirectories = additionalDirectories,
                Meta = request.Meta
            };
        return JsonSerializer.SerializeToUtf8Bytes(wireRequest, JsonContext.Default.LoadSessionRequest);
    }

    public static byte[] WritePromptRequest(string sessionId, IReadOnlyList<ContentBlock> content, PromptOptions? options = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new PromptRequest
        {
            SessionId = sessionId,
            Prompt = content.ToList(),
            Meta = CopyMetadata(options?.Metadata)
        }, JsonContext.Default.PromptRequest);

    public static byte[] WriteResumeSessionRequest(string sessionId, SessionOptions options)
    {
        List<McpServer>? mcpServers = options.McpServers.Count > 0 ? [.. options.McpServers] : null;
        List<string>? additionalDirectories = options.AdditionalDirectories.Count > 0 ? [.. options.AdditionalDirectories] : null;
        var metadata = CopyMetadata(options.Metadata);
        var wireRequest = (mcpServers is not null, additionalDirectories is not null) switch
        {
            (true, true) => new ResumeSessionRequest
            {
                SessionId = sessionId,
                Cwd = options.WorkingDirectory,
                McpServers = mcpServers,
                AdditionalDirectories = additionalDirectories,
                Meta = metadata
            },
            (true, false) => new ResumeSessionRequest
            {
                SessionId = sessionId,
                Cwd = options.WorkingDirectory,
                McpServers = mcpServers,
                Meta = metadata
            },
            (false, true) => new ResumeSessionRequest
            {
                SessionId = sessionId,
                Cwd = options.WorkingDirectory,
                AdditionalDirectories = additionalDirectories,
                Meta = metadata
            },
            _ => new ResumeSessionRequest
            {
                SessionId = sessionId,
                Cwd = options.WorkingDirectory,
                Meta = metadata
            }
        };
        return JsonSerializer.SerializeToUtf8Bytes(wireRequest, JsonContext.Default.ResumeSessionRequest);
    }

    public static byte[] WriteSetConfigOptionRequest(SetSessionConfigOptionRequest value) =>
        value.Value switch
        {
            SetSessionConfigOptionRequestCase0 boolean => JsonSerializer.SerializeToUtf8Bytes(
                boolean, JsonContext.Default.SetSessionConfigOptionRequestCase0),
            SetSessionConfigOptionRequestCase1 selection => JsonSerializer.SerializeToUtf8Bytes(
                selection, JsonContext.Default.SetSessionConfigOptionRequestCase1),
            _ => throw new ArgumentException("A string or boolean session configuration value is required.", nameof(value))
        };

    public static byte[] WritePermissionResponse(PermissionDecision decision) =>
        JsonSerializer.SerializeToUtf8Bytes(new RequestPermissionResponse
        {
            Outcome = decision.Cancelled
                ? new RequestPermissionOutcomeVariant0Cancelled()
                : new SelectedPermissionOutcome { OptionId = decision.SelectedOptionId! }
        }, JsonContext.Default.RequestPermissionResponse);

    public static string ReadNotificationSessionId(JsonElement parameters) =>
        ReadString(parameters, "sessionId") ?? throw new ProtocolException("ACP session notification did not include sessionId.");

    public static string ReadElicitationMode(JsonElement parameters) => ReadString(parameters, "mode") ?? "unknown";

    public static CreateElicitationRequest ReadElicitationRequest(JsonElement parameters) =>
        parameters.Deserialize(JsonContext.Default.CreateElicitationRequest)!;

    public static CompleteElicitationNotification ReadElicitationCompleted(JsonElement parameters) =>
        parameters.Deserialize(JsonContext.Default.CompleteElicitationNotification)!;

    /// <summary>
    /// Serializes the empty JSON object that ACP returns for methods with no response payload
    /// (fs/write_text_file, terminal/kill, terminal/release).
    /// </summary>
    public static byte[] WriteEmptyObject() => "{}"u8.ToArray();

    public static byte[] WriteTerminalExitStatusResponse(TerminalExitStatus status)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (status.ExitCode is { } exitCode) writer.WriteNumber("exitCode", exitCode);
            else writer.WriteNull("exitCode");
            if (status.Signal is { } signal) writer.WriteString("signal", signal);
            else writer.WriteNull("signal");
            if (status.Meta is { } meta)
            {
                writer.WritePropertyName("_meta");
                writer.WriteStartObject();
                foreach (var (key, value) in meta)
                {
                    writer.WritePropertyName(key);
                    value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] WriteElicitationResponse(CreateElicitationResponse response) =>
        JsonSerializer.SerializeToUtf8Bytes(response, JsonContext.Default.CreateElicitationResponse);

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

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
