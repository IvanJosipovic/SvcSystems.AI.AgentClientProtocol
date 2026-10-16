using System.Text.Json;

namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Base type for normalized updates received during an ACP prompt turn.</summary>
public abstract record AcpSessionUpdate;

/// <summary>A streamed user, assistant, or thought content block.</summary>
public sealed record AcpContentUpdate(string Role, AcpContentBlock Content, string? MessageId) : AcpSessionUpdate;

/// <summary>A tool call was created or updated by the agent.</summary>
public sealed record AcpToolCallUpdate(
    string Id,
    string? Name,
    string? Title,
    string? Kind,
    string? Status,
    bool IsUpdate,
    IReadOnlyList<AcpContentBlock>? Content = null,
    IReadOnlyList<AcpToolCallLocation>? Locations = null,
    JsonElement? RawInput = null,
    JsonElement? RawOutput = null) : AcpSessionUpdate;

/// <summary>File location reported by an ACP tool call.</summary>
public sealed record AcpToolCallLocation(string Path, uint? Line);

/// <summary>The agent's complete current execution plan.</summary>
public sealed record AcpPlanUpdate(IReadOnlyList<AcpPlanEntry> Entries) : AcpSessionUpdate;

/// <summary>An update to agent-provided slash commands.</summary>
public sealed record AcpAvailableCommandsUpdate(IReadOnlyList<AcpAvailableCommand> Commands) : AcpSessionUpdate;

/// <summary>The agent changed the session's active mode.</summary>
public sealed record AcpModeUpdate(string CurrentModeId) : AcpSessionUpdate;

/// <summary>The agent updated session metadata.</summary>
public sealed record AcpSessionInfoUpdate(string? Title, string? UpdatedAt) : AcpSessionUpdate;

/// <summary>The agent reported context usage.</summary>
public sealed record AcpUsageUpdate(ulong Used, ulong Size, double? CostAmount = null, string? CostCurrency = null) : AcpSessionUpdate;

/// <summary>A user-facing, out-of-history notice.</summary>
public sealed record AcpNoticeUpdate(string Severity, string Title, string? Description) : AcpSessionUpdate;

/// <summary>The agent's full current set of session configuration options.</summary>
public sealed record AcpConfigOptionsUpdate(IReadOnlyList<AcpConfigOption> Options) : AcpSessionUpdate;

/// <summary>Agent-advertised session configuration option.</summary>
public sealed record AcpConfigOption(
    string Id,
    string Type,
    string Name,
    string? Description,
    string? Category,
    AcpConfigValue? CurrentValue,
    IReadOnlyList<AcpConfigOptionItem>? Options);

/// <summary>Selectable session configuration value.</summary>
public sealed record AcpConfigOptionItem(string ValueId, string Name, string? Description, string? Group = null);

/// <summary>Agent context-compaction lifecycle update.</summary>
public sealed record AcpCompactionUpdate(string Id, string Status, IReadOnlyList<AcpContentBlock>? Summary, string? Error) : AcpSessionUpdate;

/// <summary>Content appended to an agent context-compaction summary.</summary>
public sealed record AcpCompactionSummaryUpdate(string Id, AcpContentBlock Content) : AcpSessionUpdate;

/// <summary>A protocol update kind not represented by a specialized public update type.</summary>
public sealed record AcpUnmappedUpdate(string Kind, JsonElement? RawPayload = null) : AcpSessionUpdate;

/// <summary>The prompt turn ended and includes the ACP stop reason.</summary>
public sealed record AcpCompleted(string Reason) : AcpSessionUpdate;

/// <summary>One task in an agent execution plan.</summary>
public sealed record AcpPlanEntry(string Content, string Status, string? Priority = null);

/// <summary>A command advertised by the agent.</summary>
public sealed record AcpAvailableCommand(string Name, string Description, string? InputHint);

/// <summary>Base type for content blocks accepted by ACP prompt methods.</summary>
public abstract record AcpContentBlock;

/// <summary>Text content.</summary>
public sealed record AcpTextContent(string Text) : AcpContentBlock;

/// <summary>Base64-encoded image content.</summary>
public sealed record AcpImageContent(string Data, string MimeType, string? Uri = null) : AcpContentBlock;

/// <summary>Base64-encoded audio content.</summary>
public sealed record AcpAudioContent(string Data, string MimeType) : AcpContentBlock;

/// <summary>A link to an agent-readable resource.</summary>
public sealed record AcpResourceLink(string Name, string Uri, string? Description = null, string? MimeType = null) : AcpContentBlock;

/// <summary>Embedded text or binary resource content.</summary>
public sealed record AcpEmbeddedResource(string Uri, string? Text, string? Blob, string? MimeType) : AcpContentBlock;

/// <summary>A file diff emitted as tool-call content.</summary>
public sealed record AcpDiffContent(string Path, string? OldText, string NewText) : AcpContentBlock;

/// <summary>A terminal embedded in a tool call.</summary>
public sealed record AcpTerminalReference(string TerminalId) : AcpContentBlock;

/// <summary>Unknown forward-compatible content block preserved as JSON.</summary>
public sealed record AcpUnknownContent(string Type, JsonElement RawPayload) : AcpContentBlock;
