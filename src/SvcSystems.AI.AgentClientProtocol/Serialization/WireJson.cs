using System.Text.Json;
using SvcSystems.AI.AgentClientProtocol.Protocol.Generated;

namespace SvcSystems.AI.AgentClientProtocol.Serialization;

internal static class WireJson
{
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

        return GeneratedJson.Serialize(new InitializeRequest
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
        });
    }

    public static byte[] WriteNewSessionRequest(NewSessionRequest request, IReadOnlyList<McpServerOptions> mcpServers) =>
        GeneratedJson.Serialize(new NewSessionRequest
        {
            Cwd = request.Cwd,
            McpServers = ClientAdapters.ToMcpServers(mcpServers),
            AdditionalDirectories = request.AdditionalDirectories is { Count: > 0 } ? [.. request.AdditionalDirectories] : null,
            Meta = request.Meta,
            ExtensionData = request.ExtensionData
        });

    public static byte[] WriteLoadSessionRequest(LoadSessionRequest request, IReadOnlyList<McpServerOptions> mcpServers) =>
        GeneratedJson.Serialize(new LoadSessionRequest
        {
            SessionId = request.SessionId,
            Cwd = request.Cwd,
            McpServers = ClientAdapters.ToMcpServers(mcpServers),
            AdditionalDirectories = request.AdditionalDirectories is { Count: > 0 } ? [.. request.AdditionalDirectories] : null,
            Meta = request.Meta,
            ExtensionData = request.ExtensionData
        });

    public static byte[] WritePromptRequest(string sessionId, IReadOnlyList<AgentContentBlock> content) =>
        GeneratedJson.Serialize(new PromptRequest
        {
            SessionId = sessionId,
            Prompt = content.Select(ClientAdapters.ToContentBlock).ToList()
        });

    public static byte[] WriteCancelNotification(CancelNotification notification) => GeneratedJson.Serialize(notification);

    public static byte[] WriteAuthenticateRequest(string methodId) => GeneratedJson.Serialize(new AuthenticateRequest { MethodId = methodId });

    public static byte[] WriteLogoutRequest() => GeneratedJson.Serialize(new LogoutRequest());

    public static byte[] WriteListSessionsRequest(string? workingDirectory, string? cursor) =>
        GeneratedJson.Serialize(new ListSessionsRequest { Cwd = workingDirectory, Cursor = cursor });

    public static byte[] WriteSessionIdRequest(string sessionId) => GeneratedJson.Serialize(new DeleteSessionRequest { SessionId = sessionId });

    public static byte[] WriteResumeSessionRequest(string sessionId, SessionOptions options) =>
        GeneratedJson.Serialize(new ResumeSessionRequest
        {
            SessionId = sessionId,
            Cwd = options.WorkingDirectory,
            McpServers = options.McpServers.Count > 0 ? ClientAdapters.ToMcpServers(options.McpServers) : null,
            AdditionalDirectories = options.AdditionalDirectories.Count > 0 ? [.. options.AdditionalDirectories] : null
        });

    public static byte[] WriteSetModeRequest(string sessionId, string modeId) =>
        GeneratedJson.Serialize(new SetSessionModeRequest { SessionId = sessionId, ModeId = modeId });

    public static byte[] WriteSetConfigOptionRequest(string sessionId, string configId, ConfigValue value)
    {
        var common = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["sessionId"] = ToJsonElement(sessionId),
            ["configId"] = ToJsonElement(configId)
        };
        return value.Boolean is { } boolean
            ? GeneratedJson.Serialize(new SetSessionConfigOptionRequestCase0
            {
                Value = boolean,
                ExtensionData = common
            })
            : GeneratedJson.Serialize(new SetSessionConfigOptionRequestCase1 { Value = value.ValueId!, ExtensionData = common });
    }

    public static byte[] WritePermissionResponse(PermissionDecision decision) =>
        GeneratedJson.Serialize(new RequestPermissionResponse
        {
            Outcome = decision.Cancelled
                ? new RequestPermissionOutcomeVariant0Cancelled()
                : new RequestPermissionOutcomeVariant1Selected { OptionId = decision.SelectedOptionId! }
        });

    public static AgentInfo ReadAgentInfo(JsonElement response)
    {
        var protocolVersion = response.TryGetProperty("protocolVersion", out var versionValue) && versionValue.TryGetInt32(out var version)
            ? version
            : throw new ProtocolException("initialize response did not contain a valid protocolVersion.");
        var implementation = response.TryGetProperty("agentInfo", out var infoElement) && infoElement.ValueKind == JsonValueKind.Object
            ? GeneratedJson.DeserializeImplementation(infoElement)
            : null;
        var info = implementation ?? new Implementation { Name = "Unknown ACP agent", Version = "unknown" };

        var capabilities = response.TryGetProperty("agentCapabilities", out var capabilityElement)
            ? GeneratedJson.DeserializeAgentCapabilities(capabilityElement)
            : new AgentCapabilities();

        var authMethods = new List<AuthenticationMethod>();
        if (response.TryGetProperty("authMethods", out var methodsElement) && methodsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var method in methodsElement.EnumerateArray())
            {
                var id = ReadString(method, "id");
                var name = ReadString(method, "name");
                if (id is not null && name is not null)
                {
                    string? type = ReadString(method, "type");
                    IReadOnlyList<string>? arguments = null;
                    IReadOnlyDictionary<string, string>? environment = null;
                    if (method.TryGetProperty("args", out _))
                    {
                        var terminal = GeneratedJson.DeserializeAuthMethodTerminal(method);
                        arguments = terminal.Args;
                        environment = terminal.Env;
                        type ??= ReadString(terminal.ExtensionData, "type");
                    }
                    else
                    {
                        var agent = GeneratedJson.DeserializeAuthMethodAgent(method);
                        type ??= ReadString(agent.ExtensionData, "type");
                    }
                    authMethods.Add(new AuthenticationMethod(
                        id,
                        name,
                        ReadString(method, "description"),
                        type,
                        arguments,
                        environment));
                }
            }
        }

        return new AgentInfo(info, protocolVersion, capabilities, authMethods);
    }

    public static ListSessionsResponse ReadSessionListPage(JsonElement response) =>
        GeneratedJson.DeserializeListSessionsResponse(response);

    public static void ApplySessionInitialState(Session session, JsonElement response)
    {
        var modeState = response.TryGetProperty("modes", out var modes) && modes.ValueKind == JsonValueKind.Object
            ? GeneratedJson.DeserializeSessionModeState(modes)
            : null;

        var configOptions = response.TryGetProperty("configOptions", out var config) && config.ValueKind == JsonValueKind.Array
            ? ReadConfigOptions(response)
            : null;
        session.SetInitialState(modeState, configOptions);
    }

    public static string ReadSessionId(JsonElement response) =>
        ReadString(response, "sessionId") ?? throw new ProtocolException("session/new response did not contain a sessionId.");

    public static string ReadStopReason(JsonElement response) =>
        GeneratedJson.DeserializePromptResponse(response).StopReason;

    public static string ReadNotificationSessionId(JsonElement parameters) =>
        ReadString(parameters, "sessionId") ?? throw new ProtocolException("ACP session notification did not include sessionId.");

    public static PermissionRequest ReadPermissionRequest(JsonElement parameters)
    {
        var request = GeneratedJson.DeserializeRequestPermissionRequest(parameters);
        return new PermissionRequest(
            request.SessionId,
            new PermissionToolCall(request.ToolCall.ToolCallId, request.ToolCall.Title, request.ToolCall.Name, request.ToolCall.Status),
            request.Options.Select(option => new PermissionChoice(option.OptionId, option.Name, option.Kind)).ToArray());
    }

    public static ReadTextFileRequest ReadTextFileReadRequest(JsonElement parameters) =>
        GeneratedJson.DeserializeReadTextFileRequest(parameters);

    public static WriteTextFileRequest ReadTextFileWriteRequest(JsonElement parameters) =>
        GeneratedJson.DeserializeWriteTextFileRequest(parameters);

    public static CreateTerminalRequest ReadTerminalCreateRequest(JsonElement parameters) =>
        GeneratedJson.DeserializeCreateTerminalRequest(parameters);

    public static (string SessionId, string TerminalId) ReadTerminalRequest(JsonElement parameters)
    {
        var request = GeneratedJson.DeserializeTerminalOutputRequest(parameters);
        return (request.SessionId, request.TerminalId);
    }

    public static ElicitationRequest ReadElicitationRequest(JsonElement parameters)
    {
        var mode = ReadString(parameters, "mode") ?? "unknown";
        JsonElement? requestedSchema = parameters.TryGetProperty("requestedSchema", out var schema) ? schema.Clone() : null;
        return new ElicitationRequest(
            mode,
            ReadString(parameters, "message") ?? string.Empty,
            ReadString(parameters, "sessionId"),
            ReadString(parameters, "toolCallId"),
            ReadString(parameters, "requestId"),
            ReadString(parameters, "elicitationId"),
            ReadString(parameters, "url"),
            requestedSchema);
    }

    public static ElicitationCompleted ReadElicitationCompleted(JsonElement parameters) =>
        new(GeneratedJson.DeserializeCompleteElicitationNotification(parameters).ElicitationId);

    public static byte[] WriteTextFileReadResponse(string content) =>
        GeneratedJson.Serialize(new ReadTextFileResponse { Content = content });

    public static byte[] WriteEmptyResponse() => GeneratedJson.Serialize(new WriteTextFileResponse());

    public static byte[] WriteTerminalCreateResponse(string terminalId) =>
        GeneratedJson.Serialize(new CreateTerminalResponse { TerminalId = terminalId });

    public static byte[] WriteTerminalOutputResponse(TerminalOutputResponse output) =>
        GeneratedJson.Serialize(output);

    public static byte[] WriteTerminalExitStatusResponse(TerminalExitStatus status) =>
        GeneratedJson.Serialize(new TerminalExitStatus
        {
            ExitCode = status.ExitCode,
            Signal = status.Signal,
            ExtensionData = MergeNullOptionalValues(status.ExtensionData, status.ExitCode is null, status.Signal is null)
        });

    public static byte[] WriteElicitationResponse(ElicitationResponse response)
    {
        var extensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["action"] = ToJsonElement(response.Action)
        };
        if (response.Content is { } content) extensionData["content"] = content;

        CreateElicitationResponse model = Discriminators.ParseCreateElicitationResponseDiscriminator(response.Action) switch
        {
            CreateElicitationResponseDiscriminator.Accept => new ElicitationAcceptAction { ExtensionData = extensionData },
            CreateElicitationResponseDiscriminator.Decline => new CreateElicitationResponseCase1(),
            CreateElicitationResponseDiscriminator.Cancel => new CreateElicitationResponseCase2(),
            _ => new CreateElicitationResponseCase3 { Action = response.Action }
        };
        return GeneratedJson.Serialize(model);
    }

    public static SessionEvent ReadSessionUpdate(JsonElement notification)
    {
        if (!notification.TryGetProperty("update", out var update) || update.ValueKind != JsonValueKind.Object)
        {
            throw new ProtocolException("session/update notification did not include an update object.");
        }

        var kind = ReadString(update, "sessionUpdate") ?? throw new ProtocolException("session/update did not include the sessionUpdate discriminator.");
        var updateKind = Discriminators.ParseSessionUpdateDiscriminator(kind);
        if (updateKind == SessionUpdateDiscriminator.Unknown) return new UnmappedUpdate(kind, update.Clone());

        if (updateKind is SessionUpdateDiscriminator.ToolCall or SessionUpdateDiscriminator.ToolCallUpdate)
        {
            if (HasUnknownToolCallContent(update))
            {
                return new AgentToolCallUpdate(
                    ReadString(update, "toolCallId") ?? string.Empty,
                    ReadString(update, "name"),
                    ReadString(update, "title"),
                    ReadString(update, "kind"),
                    ReadString(update, "status"),
                    IsUpdate: updateKind == SessionUpdateDiscriminator.ToolCallUpdate,
                    Content: ReadToolCallContent(update),
                    Locations: ReadToolCallLocations(update),
                    RawInput: ReadOptionalElement(update, "rawInput"),
                    RawOutput: ReadOptionalElement(update, "rawOutput"));
            }
            return updateKind == SessionUpdateDiscriminator.ToolCall
                ? ToAgentToolCallUpdate(GeneratedJson.DeserializeSessionUpdateVariant3ToolCall(update))
                : ToAgentToolCallUpdate(GeneratedJson.DeserializeSessionUpdateVariant4ToolCallUpdate(update));
        }

        if (updateKind == SessionUpdateDiscriminator.ConfigOptionUpdate)
            return new ConfigOptionsUpdate(ReadConfigOptions(update));

        return GeneratedJson.DeserializeSessionUpdate(update) switch
        {
            SessionUpdateVariant0UserMessageChunk value => new ContentUpdate("user", ClientAdapters.ToAgentContent(value.Content), value.MessageId),
            SessionUpdateVariant1AgentMessageChunk value => new ContentUpdate("agent", ClientAdapters.ToAgentContent(value.Content), value.MessageId),
            SessionUpdateVariant2AgentThoughtChunk value => new ContentUpdate("thought", ClientAdapters.ToAgentContent(value.Content), value.MessageId),
            SessionUpdateVariant5Plan value => new PlanUpdate(value.Entries),
            SessionUpdateVariant6AvailableCommandsUpdate value => new AgentCommandsUpdate(
                value.AvailableCommands.Select(command => new AgentCommand(command.Name, command.Description, command.Input?.Hint)).ToArray()),
            SessionUpdateVariant7CurrentModeUpdate value => new ModeUpdate(value.CurrentModeId),
            SessionUpdateVariant9SessionInfoUpdate value => new SessionMetadataUpdate(value.Title, value.UpdatedAt),
            SessionUpdateVariant10UsageUpdate value => new TokenUsageUpdate(value.Used, value.Size, value.Cost?.Amount, value.Cost?.Currency),
            SessionUpdateVariant11Notice value => new NoticeUpdate(value.Severity, value.Title, value.Description),
            SessionUpdateVariant12CompactionUpdate value => new ContextCompactionUpdate(
                value.CompactionId,
                value.Status,
                value.Summary?.Select(ClientAdapters.ToAgentContent).ToArray(),
                value.Error),
            SessionUpdateVariant13CompactionSummaryChunk value => new CompactionSummaryUpdate(value.CompactionId, ClientAdapters.ToAgentContent(value.Content)),
            _ => new UnmappedUpdate(kind, update.Clone())
        };
    }

    private static AgentToolCallUpdate ToAgentToolCallUpdate(SessionUpdateVariant3ToolCall value) =>
        new(value.ToolCallId, value.Name, value.Title, value.Kind, value.Status, false,
            value.Content?.Select(ClientAdapters.ToAgentToolCallContent).ToArray(), value.Locations, value.RawInput, value.RawOutput);

    private static AgentToolCallUpdate ToAgentToolCallUpdate(SessionUpdateVariant4ToolCallUpdate value) =>
        new(value.ToolCallId, value.Name, value.Title, value.Kind, value.Status, true,
            value.Content?.Select(ClientAdapters.ToAgentToolCallContent).ToArray(), value.Locations, value.RawInput, value.RawOutput);

    private static bool HasUnknownToolCallContent(JsonElement update) =>
        update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array &&
        content.EnumerateArray().Any(item => Discriminators.ParseToolCallContentDiscriminator(ReadString(item, "type")) == ToolCallContentDiscriminator.Unknown);

    public static IReadOnlyList<SessionConfigOption> ReadConfigOptionsResponse(JsonElement response) =>
        ReadConfigOptions(response);

    private static IReadOnlyList<SessionConfigOption> ReadConfigOptions(JsonElement update)
    {
        if (!update.TryGetProperty("configOptions", out var array) || array.ValueKind != JsonValueKind.Array) return [];
        return array.EnumerateArray().Select(option =>
        {
            var type = ReadString(option, "type");
            return Discriminators.ParseSessionConfigOptionDiscriminator(type) == SessionConfigOptionDiscriminator.Unknown
                ? new UnknownSessionConfigOption(option.Clone())
                : GeneratedJson.DeserializeSessionConfigOption(option);
        }).ToArray();
    }

    private static IReadOnlyList<AgentContentBlock>? ReadToolCallContent(JsonElement update)
    {
        if (!update.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
        var result = new List<AgentContentBlock>();
        foreach (var item in content.EnumerateArray())
        {
            switch (Discriminators.ParseToolCallContentDiscriminator(ReadString(item, "type")))
            {
                case ToolCallContentDiscriminator.Content when item.TryGetProperty("content", out var block):
                    result.Add(ReadContentBlock(block));
                    break;
                case ToolCallContentDiscriminator.Diff:
                    var diff = GeneratedJson.DeserializeToolCallContentVariant1Diff(item);
                    result.Add(new DiffContent(diff.Path, diff.OldText, diff.NewText));
                    break;
                case ToolCallContentDiscriminator.Terminal:
                    var terminal = GeneratedJson.DeserializeToolCallContentVariant2Terminal(item);
                    result.Add(new TerminalReference(terminal.TerminalId));
                    break;
                default:
                    var type = ReadString(item, "type") ?? "unknown";
                    result.Add(new UnknownContent(type, item.Clone()));
                    break;
            }
        }
        return result;
    }

    private static IReadOnlyList<ToolCallLocation>? ReadToolCallLocations(JsonElement update)
    {
        if (!update.TryGetProperty("locations", out var locations) || locations.ValueKind != JsonValueKind.Array) return null;
        return locations.EnumerateArray()
            .Select(location => GeneratedJson.DeserializeToolCallLocation(location))
            .ToArray();
    }

    private static AgentContentBlock ReadContentBlock(JsonElement element) =>
        ClientAdapters.ToAgentContent(GeneratedJson.DeserializeContentBlock(element));

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

    private static JsonElement ToJsonElement(string value) =>
        JsonDocument.Parse($"\"{JsonEncodedText.Encode(value)}\"").RootElement.Clone();

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadString(IReadOnlyDictionary<string, JsonElement>? element, string propertyName) =>
        element is not null && element.TryGetValue(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonElement? ReadOptionalElement(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) ? value.Clone() : null;

}

internal sealed class UnknownWireContent(JsonElement rawPayload) : ContentBlock
{
    public JsonElement RawPayload { get; } = rawPayload;
}
