using System.Text.Json;

namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Host-provided handlers for form and URL elicitation requests.</summary>
public interface IElicitationHandler
{
    /// <summary>Whether the host can present form-based elicitation.</summary>
    bool SupportsForm { get; }

    /// <summary>Whether the host can present URL-based elicitation.</summary>
    bool SupportsUrl { get; }

    /// <summary>Requests user input for a form or URL interaction.</summary>
    ValueTask<ElicitationResponse> RequestElicitationAsync(ElicitationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Normalized elicitation request received from an agent.</summary>
public sealed record ElicitationRequest(
    string Mode,
    string Message,
    string? SessionId,
    string? ToolCallId,
    string? RequestId,
    string? ElicitationId,
    string? Url,
    JsonElement? RequestedSchema);

/// <summary>Host response to an elicitation request.</summary>
public sealed record ElicitationResponse(string Action, JsonElement? Content = null);

/// <summary>Notification that a URL elicitation completed.</summary>
public sealed record ElicitationCompleted(string ElicitationId);
