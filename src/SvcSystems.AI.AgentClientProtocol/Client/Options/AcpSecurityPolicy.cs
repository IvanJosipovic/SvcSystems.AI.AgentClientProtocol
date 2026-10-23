namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Host-controlled policy for the commands and network endpoints an ACP agent may be given.</summary>
/// <remarks>
/// <para>
/// The agent is an untrusted peer, but the client is the trusted side. Two kinds of client-supplied
/// configuration nevertheless reach the agent and can make it act on the host's behalf: MCP server
/// definitions and terminal invocations. A host that builds either from workspace files, saved
/// settings, or user input must decide what the agent is allowed to run and reach.
/// </para>
/// <para>
/// This policy is evaluated on the client before the request is sent, so a rejected value never
/// reaches the agent. Every check fails closed: a value is allowed only when a predicate explicitly
/// accepts it.
/// </para>
/// <para>
/// <b>DNS Rebinding Limitation:</b> Hostname-based endpoints (e.g., <c>https://mcp.internal/</c>)
/// are not resolved during validation. They pass the policy check but may resolve to a private IP
/// (127.0.0.1, 10.x.x.x, etc.) at connection time. Applications using hostnames should implement
/// their own DNS resolution with IP validation, or use IP literals in policies. For the strongest
/// protection, use IP address literals in <see cref="McpEndpointAllowed"/> or restrict to explicit
/// hostnames via <see cref="AllowMcpHosts"/>.
/// </para>
/// </remarks>
public sealed class AcpSecurityPolicy
{
    /// <summary>A policy that rejects every MCP command and network endpoint.</summary>
    public static AcpSecurityPolicy DenyAll { get; } = new();

    /// <summary>
    /// Gets or sets the predicate applied to an MCP stdio server command.
    /// </summary>
    /// <remarks>
    /// The command is the executable the agent will launch. The default rejects everything, so an
    /// MCP server can only be requested when the host opts in.
    /// </remarks>
    public Func<string, bool> McpCommandAllowed { get; init; } = static _ => false;

    /// <summary>
    /// Gets or sets the predicate applied to an MCP HTTP or SSE endpoint.
    /// </summary>
    /// <remarks>
    /// The default rejects everything. A host that permits remote MCP servers should at minimum
    /// restrict the scheme to HTTPS and reject loopback, link-local, and private address ranges
    /// unless those are explicitly wanted.
    /// </remarks>
    public Func<Uri, bool> McpEndpointAllowed { get; init; } = static _ => false;

    /// <summary>
    /// Gets or sets the predicate applied to a terminal command the agent asks the host to run.
    /// </summary>
    /// <remarks>
    /// The default rejects everything. The predicate receives the command and its arguments.
    /// </remarks>
    public Func<string, IReadOnlyList<string>, bool> TerminalCommandAllowed { get; init; } = static (_, _) => false;

    /// <summary>Allows an MCP stdio command only when it is one of the listed executables.</summary>
    /// <param name="allowedCommands">Absolute paths of the executables that may be launched.</param>
    /// <returns>A policy that permits only the listed commands.</returns>
    public static AcpSecurityPolicy AllowMcpCommands(params string[] allowedCommands) => new()
    {
        McpCommandAllowed = command => allowedCommands.Contains(command, StringComparer.Ordinal)
    };

    /// <summary>Allows an MCP endpoint only when its host is one of the listed hosts.</summary>
    /// <param name="allowedHosts">The hosts that may be contacted.</param>
    /// <returns>A policy that permits only the listed hosts.</returns>
    public static AcpSecurityPolicy AllowMcpHosts(params string[] allowedHosts) => new()
    {
        McpEndpointAllowed = endpoint => allowedHosts.Contains(endpoint.Host, StringComparer.OrdinalIgnoreCase)
    };

    /// <summary>
    /// Allows an MCP endpoint only when it is an HTTPS URL that is not a loopback, link-local, or
    /// private address.
    /// </summary>
    /// <remarks>
    /// This is the safe default for a host that wants remote MCP servers but does not want to
    /// expose internal services or cloud instance metadata to an agent.
    /// </remarks>
    /// <returns>A policy that permits public HTTPS MCP endpoints only.</returns>
    public static AcpSecurityPolicy AllowPublicHttpsMcpEndpoints() => new()
    {
        McpEndpointAllowed = static endpoint =>
            endpoint.Scheme == Uri.UriSchemeHttps && !IsRestrictedAddress(endpoint)
    };

    /// <summary>
    /// Determines whether an endpoint resolves to an address that must not be reached.
    /// </summary>
    /// <param name="endpoint">The endpoint to classify.</param>
    /// <returns><see langword="true"/> when the endpoint targets a restricted address range.</returns>
    public static bool IsRestrictedAddress(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri) return true;
        if (endpoint.IsLoopback) return true;

        var host = endpoint.DnsSafeHost;
        if (string.IsNullOrEmpty(host)) return true;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;

        if (System.Net.IPAddress.TryParse(host, out var address))
        {
            return IsRestrictedAddress(address);
        }

        // A named host can still resolve to a restricted address, but resolving it here would
        // introduce a DNS lookup into configuration validation, which can block and can be
        // rebinding-attacked. Hosts that need name-based filtering should do it themselves.
        return false;
    }

    /// <summary>
    /// Determines whether an IP address is in a range that must not be reached.
    /// </summary>
    /// <param name="address">The address to classify.</param>
    /// <returns><see langword="true"/> when the address is loopback, link-local, or private.</returns>
    public static bool IsRestrictedAddress(System.Net.IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (System.Net.IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily is System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return true;
            // Unique local addresses (fc00::/7).
            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true;
            // IPv4-mapped addresses (::ffff:w.x.y.z) - delegate to IPv4 classification.
            if (address.IsIPv4MappedToIPv6) return IsRestrictedAddress(address.MapToIPv4());
            // IPv4-compatible addresses (::w.x.y.z, i.e. ::/96) and the unspecified address ::.
            // Treat the entire ::/96 range as restricted by mapping the last 4 bytes to IPv4.
            bool isIpv4Compatible = true;
            for (int i = 0; i < 12; i++)
            {
                if (bytes[i] != 0) { isIpv4Compatible = false; break; }
            }
            if (isIpv4Compatible)
            {
                var mapped = new System.Net.IPAddress(new byte[] { bytes[12], bytes[13], bytes[14], bytes[15] });
                return IsRestrictedAddress(mapped);
            }
            return false;
        }

        var octets = address.GetAddressBytes();
        if (octets[0] == 10) return true;                                   // 10.0.0.0/8
        if (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31) return true; // 172.16.0.0/12
        if (octets[0] == 192 && octets[1] == 168) return true;             // 192.168.0.0/16
        if (octets[0] == 169 && octets[1] == 254) return true;             // 169.254.0.0/16
        if (octets[0] == 100 && octets[1] >= 64 && octets[1] <= 127) return true; // 100.64.0.0/10
        if (octets[0] == 0) return true;                                   // 0.0.0.0/8
        return false;
    }
}
