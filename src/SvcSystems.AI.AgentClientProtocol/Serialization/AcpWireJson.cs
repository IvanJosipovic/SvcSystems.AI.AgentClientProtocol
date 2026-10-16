using System.Buffers;
using System.Text.Json;
using SvcSystems.AI.AgentClientProtocol.Protocol.Generated;

namespace SvcSystems.AI.AgentClientProtocol.Serialization;

internal static class AcpWireJson
{
    public static byte[] WriteInitializeRequest(InitializeRequest request, AcpImplementationInfo clientInfo, AcpClientOptions options)
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

        return AcpGeneratedJson.Serialize(new InitializeRequest
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
            ClientInfo = new Implementation { Name = clientInfo.Name, Version = clientInfo.Version },
            Meta = request.Meta,
            ExtensionData = request.ExtensionData
        });
    }

    public static byte[] WriteNewSessionRequest(NewSessionRequest request, IReadOnlyList<AcpMcpServerOptions> mcpServers) =>
        AcpGeneratedJson.Serialize(new NewSessionRequest
        {
            Cwd = request.Cwd,
            McpServers = ToMcpServers(mcpServers),
            AdditionalDirectories = request.AdditionalDirectories is { Count: > 0 } ? [.. request.AdditionalDirectories] : null,
            Meta = request.Meta,
            ExtensionData = request.ExtensionData
        });

    public static byte[] WriteLoadSessionRequest(LoadSessionRequest request, IReadOnlyList<AcpMcpServerOptions> mcpServers) =>
        AcpGeneratedJson.Serialize(new LoadSessionRequest
        {
            SessionId = request.SessionId,
            Cwd = request.Cwd,
            McpServers = ToMcpServers(mcpServers),
            AdditionalDirectories = request.AdditionalDirectories is { Count: > 0 } ? [.. request.AdditionalDirectories] : null,
            Meta = request.Meta,
            ExtensionData = request.ExtensionData
        });

    public static byte[] WritePromptRequest(string sessionId, IReadOnlyList<AcpContentBlock> content)
    {
        if (content.Any(block => block is AcpUnknownContent))
        {
            return Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("sessionId", sessionId);
                writer.WritePropertyName("prompt");
                writer.WriteStartArray();
                foreach (var block in content)
                {
                    if (block is AcpUnknownContent unknown) unknown.RawPayload.WriteTo(writer);
                    else writer.WriteRawValue(AcpGeneratedJson.Serialize(ToContentBlock(block)), skipInputValidation: true);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            });
        }

        return AcpGeneratedJson.Serialize(new PromptRequest
        {
            SessionId = sessionId,
            Prompt = content.Select(ToContentBlock).ToList()
        });
    }

    public static byte[] WriteCancelNotification(CancelNotification notification) => AcpGeneratedJson.Serialize(notification);

    public static byte[] WriteAuthenticateRequest(string methodId) => AcpGeneratedJson.Serialize(new AuthenticateRequest { MethodId = methodId });

    public static byte[] WriteLogoutRequest() => AcpGeneratedJson.Serialize(new LogoutRequest());

    public static byte[] WriteListSessionsRequest(string? workingDirectory, string? cursor) =>
        AcpGeneratedJson.Serialize(new ListSessionsRequest { Cwd = workingDirectory, Cursor = cursor });

    public static byte[] WriteSessionIdRequest(string sessionId) => AcpGeneratedJson.Serialize(new DeleteSessionRequest { SessionId = sessionId });

    public static byte[] WriteResumeSessionRequest(string sessionId, AcpSessionOptions options) =>
        AcpGeneratedJson.Serialize(new ResumeSessionRequest
        {
            SessionId = sessionId,
            Cwd = options.WorkingDirectory,
            McpServers = options.McpServers.Count > 0 ? ToMcpServers(options.McpServers) : null,
            AdditionalDirectories = options.AdditionalDirectories.Count > 0 ? [.. options.AdditionalDirectories] : null
        });

    public static byte[] WriteSetModeRequest(string sessionId, string modeId) =>
        AcpGeneratedJson.Serialize(new SetSessionModeRequest { SessionId = sessionId, ModeId = modeId });

    public static byte[] WriteSetConfigOptionRequest(string sessionId, string configId, AcpConfigValue value)
    {
        var common = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["sessionId"] = ToJsonElement(sessionId),
            ["configId"] = ToJsonElement(configId)
        };
        return value.Boolean is { } boolean
            ? AcpGeneratedJson.Serialize(new SetSessionConfigOptionRequestCase0 { Type = "boolean", Value = boolean, ExtensionData = common })
            : AcpGeneratedJson.Serialize(new SetSessionConfigOptionRequestCase1 { Value = value.ValueId!, ExtensionData = common });
    }

    public static byte[] WritePermissionResponse(AcpPermissionDecision decision) =>
        AcpGeneratedJson.Serialize(new RequestPermissionResponse
        {
            Outcome = decision.Cancelled
                ? new RequestPermissionOutcomeVariant0Cancelled()
                : new RequestPermissionOutcomeVariant1Selected { OptionId = decision.SelectedOptionId! }
        });

    public static AcpAgentInfo ReadAgentInfo(JsonElement response)
    {
        var protocolVersion = response.TryGetProperty("protocolVersion", out var versionValue) && versionValue.TryGetInt32(out var version)
            ? version
            : 0;
        var info = response.TryGetProperty("agentInfo", out var infoElement) && infoElement.ValueKind == JsonValueKind.Object
            ? new AcpImplementationInfo(ReadString(infoElement, "name") ?? "Unknown ACP agent", ReadString(infoElement, "version") ?? "unknown")
            : new AcpImplementationInfo("Unknown ACP agent", "unknown");

        var capabilities = response.TryGetProperty("agentCapabilities", out var capabilityElement)
            ? ReadAgentCapabilities(capabilityElement)
            : new AcpAgentCapabilities(false, false, false, false, new AcpSessionCapabilities(false, false, false, false, false), false, new AcpMcpCapabilities(false, false));

        var authMethods = new List<AcpAuthenticationMethod>();
        if (response.TryGetProperty("authMethods", out var methodsElement) && methodsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var method in methodsElement.EnumerateArray())
            {
                var id = ReadString(method, "id");
                var name = ReadString(method, "name");
                if (id is not null && name is not null)
                {
                    var arguments = method.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array
                        ? args.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
                        : null;
                    var environment = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (method.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var variable in env.EnumerateObject())
                        {
                            if (variable.Value.ValueKind == JsonValueKind.String) environment[variable.Name] = variable.Value.GetString()!;
                        }
                    }
                    authMethods.Add(new AcpAuthenticationMethod(
                        id,
                        name,
                        ReadString(method, "description"),
                        ReadString(method, "type"),
                        arguments,
                        environment.Count > 0 ? environment : null));
                }
            }
        }

        return new AcpAgentInfo(info.Name, info.Version, protocolVersion, capabilities, authMethods);
    }

    public static AcpSessionListPage ReadSessionListPage(JsonElement response)
    {
        var sessions = new List<AcpSessionInfo>();
        if (response.TryGetProperty("sessions", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var id = ReadString(item, "sessionId");
                var cwd = ReadString(item, "cwd");
                if (id is not null && cwd is not null)
                {
                    sessions.Add(new AcpSessionInfo(id, cwd, ReadString(item, "title"), ReadString(item, "updatedAt")));
                }
            }
        }
        return new AcpSessionListPage(sessions, ReadString(response, "nextCursor"));
    }

    public static string ReadSessionId(JsonElement response) =>
        ReadString(response, "sessionId") ?? throw new AcpProtocolException("session/new response did not contain a sessionId.");

    public static string ReadStopReason(JsonElement response) =>
        ReadString(response, "stopReason") ?? throw new AcpProtocolException("session/prompt response did not contain a stopReason.");

    public static string ReadNotificationSessionId(JsonElement parameters) =>
        ReadString(parameters, "sessionId") ?? throw new AcpProtocolException("ACP session notification did not include sessionId.");

    public static AcpPermissionRequest ReadPermissionRequest(JsonElement parameters)
    {
        var sessionId = ReadString(parameters, "sessionId") ?? throw new AcpProtocolException("Permission request did not include sessionId.");
        var toolCall = parameters.TryGetProperty("toolCall", out var toolCallElement) ? toolCallElement : default;
        var id = ReadString(toolCall, "toolCallId") ?? throw new AcpProtocolException("Permission request did not include toolCall.toolCallId.");
        var options = new List<AcpPermissionOption>();
        if (parameters.TryGetProperty("options", out var optionsElement) && optionsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in optionsElement.EnumerateArray())
            {
                var optionId = ReadString(option, "optionId");
                var name = ReadString(option, "name");
                var kind = ReadString(option, "kind");
                if (optionId is not null && name is not null && kind is not null)
                {
                    options.Add(new AcpPermissionOption(optionId, name, kind));
                }
            }
        }

        return new AcpPermissionRequest(
            sessionId,
            new AcpPermissionToolCall(id, ReadString(toolCall, "title"), ReadString(toolCall, "name"), ReadString(toolCall, "status")),
            options);
    }

    public static AcpTextFileReadRequest ReadTextFileReadRequest(JsonElement parameters) =>
        new(
            ReadString(parameters, "sessionId") ?? string.Empty,
            ReadString(parameters, "path") ?? string.Empty,
            ReadUInt32(parameters, "line"),
            ReadUInt32(parameters, "limit"));

    public static AcpTextFileWriteRequest ReadTextFileWriteRequest(JsonElement parameters) =>
        new(
            ReadString(parameters, "sessionId") ?? string.Empty,
            ReadString(parameters, "path") ?? string.Empty,
            ReadString(parameters, "content") ?? string.Empty);

    public static AcpTerminalCreateRequest ReadTerminalCreateRequest(JsonElement parameters)
    {
        var arguments = parameters.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array
            ? args.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
            : [];
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parameters.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Array)
        {
            foreach (var variable in env.EnumerateArray())
            {
                var name = ReadString(variable, "name");
                var value = ReadString(variable, "value");
                if (name is not null && value is not null) environment[name] = value;
            }
        }
        return new AcpTerminalCreateRequest(
            ReadString(parameters, "sessionId") ?? string.Empty,
            ReadString(parameters, "command") ?? string.Empty,
            arguments,
            environment,
            ReadString(parameters, "cwd"),
            ReadUInt64Nullable(parameters, "outputByteLimit"));
    }

    public static (string SessionId, string TerminalId) ReadTerminalRequest(JsonElement parameters) =>
        (ReadString(parameters, "sessionId") ?? string.Empty, ReadString(parameters, "terminalId") ?? string.Empty);

    public static AcpElicitationRequest ReadElicitationRequest(JsonElement parameters)
    {
        var mode = ReadString(parameters, "mode") ?? "unknown";
        JsonElement? requestedSchema = parameters.TryGetProperty("requestedSchema", out var schema) ? schema.Clone() : null;
        return new AcpElicitationRequest(
            mode,
            ReadString(parameters, "message") ?? string.Empty,
            ReadString(parameters, "sessionId"),
            ReadString(parameters, "toolCallId"),
            ReadString(parameters, "requestId"),
            ReadString(parameters, "elicitationId"),
            ReadString(parameters, "url"),
            requestedSchema);
    }

    public static AcpElicitationCompleted ReadElicitationCompleted(JsonElement parameters) =>
        new(ReadString(parameters, "elicitationId") ?? string.Empty);

    public static byte[] WriteTextFileReadResponse(string content) =>
        AcpGeneratedJson.Serialize(new ReadTextFileResponse { Content = content });

    public static byte[] WriteEmptyResponse() => AcpGeneratedJson.Serialize(new WriteTextFileResponse());

    public static byte[] WriteTerminalCreateResponse(string terminalId) =>
        AcpGeneratedJson.Serialize(new CreateTerminalResponse { TerminalId = terminalId });

    public static byte[] WriteTerminalOutputResponse(AcpTerminalOutput output) =>
        AcpGeneratedJson.Serialize(new TerminalOutputResponse
        {
            Output = output.Output,
            Truncated = output.Truncated,
            ExitStatus = output.ExitStatus is { } status
                ? ToTerminalExitStatus(status)
                : null
        });

    public static byte[] WriteTerminalExitStatusResponse(AcpTerminalExitStatus status) =>
        AcpGeneratedJson.Serialize(new WaitForTerminalExitResponse
        {
            ExitCode = status.ExitCode,
            Signal = status.Signal,
            ExtensionData = NullOptionalValues(status.ExitCode is null, status.Signal is null)
        });

    public static byte[] WriteElicitationResponse(AcpElicitationResponse response)
    {
        var extensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["action"] = ToJsonElement(response.Action)
        };
        if (response.Content is { } content) extensionData["content"] = content;

        CreateElicitationResponse model = response.Action switch
        {
            "accept" => new ElicitationAcceptAction { ExtensionData = extensionData },
            "decline" => new CreateElicitationResponseCase1 { Action = response.Action },
            "cancel" => new CreateElicitationResponseCase2 { Action = response.Action },
            _ => new CreateElicitationResponseCase3 { Action = response.Action }
        };
        return AcpGeneratedJson.Serialize(model);
    }

    public static AcpSessionUpdate ReadSessionUpdate(JsonElement notification)
    {
        if (!notification.TryGetProperty("update", out var update) || update.ValueKind != JsonValueKind.Object)
        {
            throw new AcpProtocolException("session/update notification did not include an update object.");
        }

        var kind = ReadString(update, "sessionUpdate") ?? throw new AcpProtocolException("session/update did not include the sessionUpdate discriminator.");
        switch (kind)
        {
            case "user_message_chunk":
            case "agent_message_chunk":
            case "agent_thought_chunk":
                if (!update.TryGetProperty("content", out var content)) throw new AcpProtocolException("Content update did not include content.");
                return new AcpContentUpdate(kind switch
                {
                    "user_message_chunk" => "user",
                    "agent_thought_chunk" => "thought",
                    _ => "agent"
                }, ReadContentBlock(content), ReadString(update, "messageId"));

            case "tool_call":
            case "tool_call_update":
                return new AcpToolCallUpdate(
                    ReadString(update, "toolCallId") ?? string.Empty,
                    ReadString(update, "name"),
                    ReadString(update, "title"),
                    ReadString(update, "kind"),
                    ReadString(update, "status"),
                    IsUpdate: kind == "tool_call_update",
                    Content: ReadToolCallContent(update),
                    Locations: ReadToolCallLocations(update),
                    RawInput: ReadOptionalElement(update, "rawInput"),
                    RawOutput: ReadOptionalElement(update, "rawOutput"));

            case "plan":
                return ReadPlanUpdate(update);

            case "available_commands_update":
                return ReadAvailableCommandsUpdate(update);

            case "current_mode_update":
                return new AcpModeUpdate(ReadString(update, "currentModeId") ?? string.Empty);

            case "session_info_update":
                return new AcpSessionInfoUpdate(ReadString(update, "title"), ReadString(update, "updatedAt"));

            case "usage_update":
                var cost = update.TryGetProperty("cost", out var costElement) ? costElement : default;
                return new AcpUsageUpdate(ReadUInt64(update, "used"), ReadUInt64(update, "size"), ReadDouble(cost, "amount"), ReadString(cost, "currency"));

            case "notice":
                return new AcpNoticeUpdate(ReadString(update, "severity") ?? "info", ReadString(update, "title") ?? string.Empty, ReadString(update, "description"));

            case "config_option_update":
                return ReadConfigOptionsUpdate(update);

            case "compaction_update":
                return ReadCompactionUpdate(update);

            case "compaction_summary_chunk":
                return new AcpCompactionSummaryUpdate(
                    ReadString(update, "compactionId") ?? string.Empty,
                    update.TryGetProperty("content", out var compactionContent) ? ReadContentBlock(compactionContent) : new AcpTextContent(string.Empty));

            default:
                return new AcpUnmappedUpdate(kind, update.Clone());
        }
    }

    private static AcpAgentCapabilities ReadAgentCapabilities(JsonElement element)
    {
        var prompt = element.TryGetProperty("promptCapabilities", out var promptElement) ? promptElement : default;
        var sessions = element.TryGetProperty("sessionCapabilities", out var sessionElement) ? sessionElement : default;
        var auth = element.TryGetProperty("auth", out var authElement) ? authElement : default;
        var mcp = element.TryGetProperty("mcpCapabilities", out var mcpElement) ? mcpElement : default;
        return new AcpAgentCapabilities(
            ReadBoolean(element, "loadSession"),
            ReadBoolean(prompt, "image"),
            ReadBoolean(prompt, "audio"),
            ReadBoolean(prompt, "embeddedContext"),
            new AcpSessionCapabilities(
                HasCapability(sessions, "list"),
                HasCapability(sessions, "delete"),
                HasCapability(sessions, "resume"),
                HasCapability(sessions, "close"),
                HasCapability(sessions, "additionalDirectories")),
            HasCapability(auth, "logout"),
            new AcpMcpCapabilities(ReadBoolean(mcp, "http"), ReadBoolean(mcp, "sse")));
    }

    private static bool HasCapability(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object;

    private static AcpPlanUpdate ReadPlanUpdate(JsonElement update)
    {
        var entries = new List<AcpPlanEntry>();
        if (update.TryGetProperty("entries", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in array.EnumerateArray())
            {
                entries.Add(new AcpPlanEntry(
                    ReadString(entry, "content") ?? string.Empty,
                    ReadString(entry, "status") ?? "pending",
                    ReadString(entry, "priority")));
            }
        }
        return new AcpPlanUpdate(entries);
    }

    private static AcpAvailableCommandsUpdate ReadAvailableCommandsUpdate(JsonElement update)
    {
        var commands = new List<AcpAvailableCommand>();
        if (update.TryGetProperty("availableCommands", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var command in array.EnumerateArray())
            {
                var inputHint = command.TryGetProperty("input", out var input) ? ReadString(input, "hint") : null;
                commands.Add(new AcpAvailableCommand(
                    ReadString(command, "name") ?? string.Empty,
                    ReadString(command, "description") ?? string.Empty,
                    inputHint));
            }
        }
        return new AcpAvailableCommandsUpdate(commands);
    }

    private static AcpConfigOptionsUpdate ReadConfigOptionsUpdate(JsonElement update)
        => new(ReadConfigOptions(update));

    public static IReadOnlyList<AcpConfigOption> ReadConfigOptionsResponse(JsonElement response) =>
        ReadConfigOptions(response);

    private static IReadOnlyList<AcpConfigOption> ReadConfigOptions(JsonElement update)
    {
        var options = new List<AcpConfigOption>();
        if (update.TryGetProperty("configOptions", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in array.EnumerateArray())
            {
                var currentValue = option.TryGetProperty("currentValue", out var current) ? ReadConfigValue(current) : null;
                options.Add(new AcpConfigOption(
                    ReadString(option, "id") ?? string.Empty,
                    ReadString(option, "type") ?? string.Empty,
                    ReadString(option, "name") ?? string.Empty,
                    ReadString(option, "description"),
                    ReadString(option, "category"),
                    currentValue,
                    ReadConfigOptionItems(option)));
            }
        }
        return options;
    }

    private static AcpConfigValue? ReadConfigValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => AcpConfigValue.FromValueId(value.GetString() ?? string.Empty),
        JsonValueKind.True => AcpConfigValue.FromBoolean(true),
        JsonValueKind.False => AcpConfigValue.FromBoolean(false),
        _ => null
    };

    private static IReadOnlyList<AcpConfigOptionItem>? ReadConfigOptionItems(JsonElement option)
    {
        if (!option.TryGetProperty("options", out var wireOptions) || wireOptions.ValueKind != JsonValueKind.Array) return null;
        var options = new List<AcpConfigOptionItem>();
        foreach (var item in wireOptions.EnumerateArray())
        {
            if (item.TryGetProperty("value", out var value))
            {
                options.Add(new AcpConfigOptionItem(
                    value.GetString() ?? string.Empty,
                    ReadString(item, "name") ?? string.Empty,
                    ReadString(item, "description")));
                continue;
            }
            if (item.TryGetProperty("options", out var groupOptions) && groupOptions.ValueKind == JsonValueKind.Array)
            {
                var groupName = ReadString(item, "name") ?? string.Empty;
                foreach (var groupedOption in groupOptions.EnumerateArray())
                {
                    options.Add(new AcpConfigOptionItem(
                        ReadString(groupedOption, "value") ?? string.Empty,
                        ReadString(groupedOption, "name") ?? string.Empty,
                        ReadString(groupedOption, "description"),
                        groupName));
                }
            }
        }
        return options;
    }

    private static AcpCompactionUpdate ReadCompactionUpdate(JsonElement update)
    {
        IReadOnlyList<AcpContentBlock>? summary = null;
        if (update.TryGetProperty("summary", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            summary = items.EnumerateArray().Select(ReadContentBlock).ToArray();
        }
        return new AcpCompactionUpdate(
            ReadString(update, "compactionId") ?? string.Empty,
            ReadString(update, "status") ?? string.Empty,
            summary,
            ReadString(update, "error"));
    }

    private static IReadOnlyList<AcpContentBlock>? ReadToolCallContent(JsonElement update)
    {
        if (!update.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
        var result = new List<AcpContentBlock>();
        foreach (var item in content.EnumerateArray())
        {
            switch (ReadString(item, "type"))
            {
                case "content" when item.TryGetProperty("content", out var block):
                    result.Add(ReadContentBlock(block));
                    break;
                case "diff":
                    result.Add(new AcpDiffContent(ReadString(item, "path") ?? string.Empty, ReadString(item, "oldText"), ReadString(item, "newText") ?? string.Empty));
                    break;
                case "terminal":
                    var terminal = item.TryGetProperty("terminalId", out var terminalId) ? terminalId.GetString() : null;
                    result.Add(new AcpTerminalReference(terminal ?? string.Empty));
                    break;
            }
        }
        return result;
    }

    private static IReadOnlyList<AcpToolCallLocation>? ReadToolCallLocations(JsonElement update)
    {
        if (!update.TryGetProperty("locations", out var locations) || locations.ValueKind != JsonValueKind.Array) return null;
        return locations.EnumerateArray()
            .Select(location => new AcpToolCallLocation(ReadString(location, "path") ?? string.Empty, ReadUInt32(location, "line")))
            .ToArray();
    }

    private static AcpContentBlock ReadContentBlock(JsonElement element)
    {
        var type = ReadString(element, "type");
        return type switch
        {
            "text" => new AcpTextContent(ReadString(element, "text") ?? string.Empty),
            "image" => new AcpImageContent(ReadString(element, "data") ?? string.Empty, ReadString(element, "mimeType") ?? "application/octet-stream", ReadString(element, "uri")),
            "audio" => new AcpAudioContent(ReadString(element, "data") ?? string.Empty, ReadString(element, "mimeType") ?? "application/octet-stream"),
            "resource_link" => new AcpResourceLink(
                ReadString(element, "name") ?? string.Empty,
                ReadString(element, "uri") ?? string.Empty,
                ReadString(element, "description"),
                ReadString(element, "mimeType")),
            "resource" => ReadEmbeddedResource(element),
            _ => new AcpUnknownContent(type ?? "unknown", element.Clone())
        };
    }

    private static AcpEmbeddedResource ReadEmbeddedResource(JsonElement element)
    {
        var resource = element.TryGetProperty("resource", out var resourceElement) ? resourceElement : default;
        return new AcpEmbeddedResource(
            ReadString(resource, "uri") ?? string.Empty,
            ReadString(resource, "text"),
            ReadString(resource, "blob"),
            ReadString(resource, "mimeType"));
    }

    private static ContentBlock ToContentBlock(AcpContentBlock block) => block switch
    {
        AcpTextContent text => new ContentBlockVariant0Text { Text = text.Text },
        AcpImageContent image => new ContentBlockVariant1Image { Data = image.Data, MimeType = image.MimeType, Uri = image.Uri },
        AcpAudioContent audio => new ContentBlockVariant2Audio { Data = audio.Data, MimeType = audio.MimeType },
        AcpResourceLink link => new ContentBlockVariant3ResourceLink
        {
            Name = link.Name,
            Uri = link.Uri,
            Description = link.Description,
            MimeType = link.MimeType
        },
        AcpEmbeddedResource resource => new ContentBlockVariant4Resource
        {
            Resource = resource.Blob is not null
                ? new BlobResourceContents { Blob = resource.Blob, MimeType = resource.MimeType, Uri = resource.Uri }
                : new TextResourceContents { Text = resource.Text ?? string.Empty, MimeType = resource.MimeType, Uri = resource.Uri }
        },
        _ => throw new ArgumentException($"Unsupported ACP prompt content type '{block.GetType().Name}'.", nameof(block))
    };

    private static List<McpServer> ToMcpServers(IReadOnlyList<AcpMcpServerOptions> servers) => servers.Select(server => server switch
    {
        AcpStdioMcpServerOptions stdio => (McpServer)new McpServerStdio
        {
            Name = stdio.Name,
            Command = stdio.Command,
            Args = stdio.Arguments?.ToList() ?? [],
            Env = stdio.Environment?.Select(item => new EnvVariable { Name = item.Key, Value = item.Value }).ToList() ?? [],
            ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["type"] = ToJsonElement("stdio") }
        },
        AcpHttpMcpServerOptions http => new McpServerHttp
        {
            Name = http.Name,
            Url = http.Url,
            Headers = ToHeaders(http.Headers),
            ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["type"] = ToJsonElement("http") }
        },
        AcpSseMcpServerOptions sse => new McpServerSse
        {
            Name = sse.Name,
            Url = sse.Url,
            Headers = ToHeaders(sse.Headers),
            ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["type"] = ToJsonElement("sse") }
        },
        _ => throw new ArgumentOutOfRangeException(nameof(servers), $"Unknown MCP server configuration type '{server.GetType().Name}'.")
    }).ToList();

    private static List<HttpHeader> ToHeaders(IReadOnlyDictionary<string, string>? headers) =>
        headers?.Select(item => new HttpHeader { Name = item.Key, Value = item.Value }).ToList() ?? [];

    private static TerminalExitStatus ToTerminalExitStatus(AcpTerminalExitStatus status) => new()
    {
        ExitCode = status.ExitCode,
        Signal = status.Signal,
        ExtensionData = NullOptionalValues(status.ExitCode is null, status.Signal is null)
    };

    private static Dictionary<string, JsonElement>? NullOptionalValues(bool exitCode, bool signal)
    {
        if (!exitCode && !signal) return null;
        using var nullValue = JsonDocument.Parse("null");
        var value = nullValue.RootElement.Clone();
        var extensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (exitCode) extensionData["exitCode"] = value;
        if (signal) extensionData["signal"] = value;
        return extensionData;
    }

    private static JsonElement ToJsonElement(string value) =>
        JsonDocument.Parse($"\"{JsonEncodedText.Encode(value)}\"").RootElement.Clone();

    private static byte[] Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBoolean(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;

    private static ulong ReadUInt64(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.TryGetUInt64(out var number) ? number : 0;

    private static JsonElement? ReadOptionalElement(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) ? value.Clone() : null;

    private static ulong? ReadUInt64Nullable(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.TryGetUInt64(out var number) ? number : null;

    private static uint? ReadUInt32(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.TryGetUInt32(out var number) ? number : null;

    private static double? ReadDouble(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) && value.TryGetDouble(out var number) ? number : null;
}
