using SvcSystems.AI.AgentClientProtocol.Transport;

namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Creates initialized ACP clients for dependency-injected consumers.</summary>
/// <remarks>The same operations are available as static members on <see cref="Client.Client"/>.</remarks>
public interface IAcpClientFactory
{
    /// <summary>Starts an agent process and completes the ACP initialize handshake.</summary>
    /// <param name="processOptions">The agent executable to launch.</param>
    /// <param name="clientOptions">Client configuration; when null, the factory's default options are used.</param>
    /// <param name="cancellationToken">A token that cancels startup and initialization.</param>
    Task<IClient> StartAsync(ProcessOptions processOptions, ClientOptions? clientOptions = null, CancellationToken cancellationToken = default);

    /// <summary>Connects over a caller-provided transport and completes the ACP initialize handshake.</summary>
    /// <param name="transport">The transport to adopt; the client disposes it.</param>
    /// <param name="clientOptions">Client configuration; when null, the factory's default options are used.</param>
    /// <param name="cancellationToken">A token that cancels initialization.</param>
    /// <param name="terminalAuthenticationProcessOptions">Process options used for terminal authentication callbacks.</param>
    Task<IClient> ConnectAsync(ITransport transport, ClientOptions? clientOptions = null, CancellationToken cancellationToken = default, ProcessOptions? terminalAuthenticationProcessOptions = null);
}
