using System.Text.Json;
using SvcSystems.AI.AgentClientProtocol.Generated;

namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Base type for normalized updates received during an ACP prompt turn.</summary>
public abstract record SessionEvent;

/// <summary>A streamed user, assistant, or thought content block.</summary>
public sealed record ContentUpdate(string Role, AgentContentBlock Content, string? MessageId) : SessionEvent;

/// <summary>A tool call was created or updated by the agent.</summary>
public sealed record AgentToolCallUpdate(
    string Id,
    string? Name,
    string? Title,
    string? Kind,
    string? Status,
    bool IsUpdate,
    IReadOnlyList<AgentContentBlock>? Content = null,
    IReadOnlyList<ToolCallLocation>? Locations = null,
    JsonElement? RawInput = null,
    JsonElement? RawOutput = null) : SessionEvent;

/// <summary>The agent's complete current execution plan.</summary>
public sealed record PlanUpdate(IReadOnlyList<PlanEntry> Entries) : SessionEvent;

/// <summary>An update to agent-provided slash commands.</summary>
public sealed record AgentCommandsUpdate(IReadOnlyList<AgentCommand> Commands) : SessionEvent;

/// <summary>The agent changed the session's active mode.</summary>
public sealed record ModeUpdate(string CurrentModeId) : SessionEvent;

/// <summary>The agent updated session metadata.</summary>
public sealed record SessionMetadataUpdate(string? Title, string? UpdatedAt) : SessionEvent;

/// <summary>The agent reported context usage.</summary>
public sealed record TokenUsageUpdate(ulong Used, ulong Size, double? CostAmount = null, string? CostCurrency = null) : SessionEvent;

/// <summary>A user-facing, out-of-history notice.</summary>
public sealed record NoticeUpdate(string Severity, string Title, string? Description) : SessionEvent;

/// <summary>The agent's full current set of session configuration options.</summary>
public sealed record ConfigOptionsUpdate(IReadOnlyList<SessionConfigOption> Options) : SessionEvent;

/// <summary>Configuration option variant not known to this schema version, preserved as raw JSON.</summary>
public sealed class UnknownSessionConfigOption(JsonElement rawPayload) : SessionConfigOption
{
    public JsonElement RawPayload { get; } = rawPayload;
}

/// <summary>Agent context-compaction lifecycle update.</summary>
public sealed record ContextCompactionUpdate(string Id, string Status, IReadOnlyList<AgentContentBlock>? Summary, string? Error) : SessionEvent;

/// <summary>Content appended to an agent context-compaction summary.</summary>
public sealed record CompactionSummaryUpdate(string Id, AgentContentBlock Content) : SessionEvent;

/// <summary>A protocol update kind not represented by a specialized public update type.</summary>
public sealed record UnmappedUpdate(string Kind, JsonElement? RawPayload = null) : SessionEvent;

/// <summary>The prompt turn ended and includes the ACP stop reason.</summary>
public sealed record Completed(string Reason) : SessionEvent;

/// <summary>A command advertised by the agent.</summary>
public sealed record AgentCommand(string Name, string Description, string? InputHint);

/// <summary>Base type for content blocks accepted by ACP prompt methods.</summary>
public abstract record AgentContentBlock;

/// <summary>Text content.</summary>
public sealed record PromptTextContent(string Text) : AgentContentBlock;

/// <summary>Base64-encoded image content.</summary>
public sealed record PromptImageContent(string Data, string MimeType, string? Uri = null) : AgentContentBlock;

/// <summary>Base64-encoded audio content.</summary>
public sealed record PromptAudioContent(string Data, string MimeType) : AgentContentBlock;

/// <summary>A link to an agent-readable resource.</summary>
public sealed record ResourceLinkContent(string Name, string Uri, string? Description = null, string? MimeType = null) : AgentContentBlock;

/// <summary>Embedded text or binary resource content.</summary>
public sealed record EmbeddedResourceContent(string Uri, string? Text, string? Blob, string? MimeType) : AgentContentBlock;

/// <summary>A file diff emitted as tool-call content.</summary>
public sealed record DiffContent(string Path, string? OldText, string NewText) : AgentContentBlock;

/// <summary>A terminal embedded in a tool call.</summary>
public sealed record TerminalReference(string TerminalId) : AgentContentBlock;

/// <summary>Unknown forward-compatible content block preserved as JSON.</summary>
public sealed record UnknownContent(string Type, JsonElement RawPayload) : AgentContentBlock;
