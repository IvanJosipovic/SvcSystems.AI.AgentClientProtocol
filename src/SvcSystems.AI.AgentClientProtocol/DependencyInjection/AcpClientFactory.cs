using SvcSystems.AI.AgentClientProtocol.Transport;
using AcpClient = SvcSystems.AI.AgentClientProtocol.Client.Client;

namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Default <see cref="IAcpClientFactory"/> backed by the static client entry points.</summary>
internal sealed class AcpClientFactory : IAcpClientFactory
{
    /// <summary>Initializes a factory that falls back to the supplied options.</summary>
    /// <param name="defaultOptions">Options applied when a caller does not supply their own.</param>
    public AcpClientFactory(ClientOptions defaultOptions)
    {
        DefaultOptions = defaultOptions ?? throw new ArgumentNullException(nameof(defaultOptions));
    }

    /// <summary>Options applied when a caller does not supply their own.</summary>
    internal ClientOptions DefaultOptions { get; }

    /// <inheritdoc />
    public async Task<IClient> StartAsync(ProcessOptions processOptions, ClientOptions? clientOptions = null, CancellationToken cancellationToken = default) =>
        await AcpClient.StartAsync(processOptions, clientOptions ?? DefaultOptions, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IClient> ConnectAsync(ITransport transport, ClientOptions? clientOptions = null, CancellationToken cancellationToken = default, ProcessOptions? terminalAuthenticationProcessOptions = null) =>
        await AcpClient.ConnectAsync(transport, clientOptions ?? DefaultOptions, cancellationToken, terminalAuthenticationProcessOptions).ConfigureAwait(false);
}
