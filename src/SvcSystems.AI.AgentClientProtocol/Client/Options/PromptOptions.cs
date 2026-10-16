using System.Text.Json;

namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Optional metadata attached to a session/prompt request.</summary>
public sealed class PromptOptions
{
    /// <summary>Request-scoped ACP _meta fields. No extension capability is enabled automatically.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }
}
