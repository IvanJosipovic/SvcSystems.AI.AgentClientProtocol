using System.Text.Json;

namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Connection-wide authentication identity reported by the optional authStatus extension.</summary>
/// <remarks>This is display information, not proof of authorization or a substitute for the authenticate response.
/// Typed members cover the members ACP defines; <see cref="RawPayload"/> is the documented escape hatch for the
/// vendor-specific fields an agent may add to the same payload.</remarks>
public sealed record AuthenticationStatus(
    string Kind,
    string Label,
    string? Detail,
    AuthenticationAccount? Account,
    JsonElement? Vendor,
    JsonElement RawPayload);

/// <summary>Optional account information reported by an agent. Values may contain personal information.</summary>
public sealed record AuthenticationAccount(string? Email, string? Organization, string? Plan);
