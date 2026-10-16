namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Permission request sent by an ACP agent before performing a tool operation.</summary>
public sealed record AcpPermissionRequest(
    string SessionId,
    AcpPermissionToolCall ToolCall,
    IReadOnlyList<AcpPermissionOption> Options);

/// <summary>Summary of the tool call requiring permission.</summary>
public sealed record AcpPermissionToolCall(string Id, string? Title, string? Name, string? Status);

/// <summary>Permission option offered by the agent.</summary>
public sealed record AcpPermissionOption(string Id, string Name, string Kind);

/// <summary>Decision returned for an agent permission request.</summary>
public sealed record AcpPermissionDecision(string? SelectedOptionId, bool Cancelled = false)
{
    /// <summary>Accepts an option advertised in the permission request.</summary>
    public static AcpPermissionDecision Select(string optionId) => new(optionId);

    /// <summary>Indicates that the prompt turn was cancelled before the user responded.</summary>
    public static AcpPermissionDecision Cancel() => new(null, Cancelled: true);
}

/// <summary>Provides host decisions for ACP permission requests.</summary>
public interface IAcpPermissionHandler
{
    /// <summary>Requests a user or policy decision for the supplied operation.</summary>
    ValueTask<AcpPermissionDecision> RequestPermissionAsync(
        AcpPermissionRequest request,
        CancellationToken cancellationToken = default);
}
