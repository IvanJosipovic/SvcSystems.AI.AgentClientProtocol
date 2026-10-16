using System.Text.Json;

namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Permission request sent by an ACP agent before performing a tool operation.</summary>
public sealed record PermissionRequest(
    string SessionId,
    PermissionToolCall ToolCall,
    IReadOnlyList<PermissionChoice> Options,
    IReadOnlyDictionary<string, JsonElement>? Meta);

/// <summary>Summary of the tool call requiring permission.</summary>
public sealed record PermissionToolCall(
    string Id,
    string? Title,
    string? Name,
    string? Kind,
    string? Status,
    JsonElement? RawInput,
    IReadOnlyDictionary<string, JsonElement>? Meta);

/// <summary>Permission option offered by the agent.</summary>
public sealed record PermissionChoice(string Id, string Name, string Kind);

/// <summary>Decision returned for an agent permission request.</summary>
public sealed record PermissionDecision(string? SelectedOptionId, bool Cancelled = false)
{
    /// <summary>Accepts an option advertised in the permission request.</summary>
    public static PermissionDecision Select(string optionId) => new(optionId);

    /// <summary>Indicates that the prompt turn was cancelled before the user responded.</summary>
    public static PermissionDecision Cancel() => new(null, Cancelled: true);
}

/// <summary>Provides host decisions for ACP permission requests.</summary>
public interface IPermissionHandler
{
    /// <summary>Requests a user or policy decision for the supplied operation.</summary>
    ValueTask<PermissionDecision> RequestPermissionAsync(
        PermissionRequest request,
        CancellationToken cancellationToken = default);
}
