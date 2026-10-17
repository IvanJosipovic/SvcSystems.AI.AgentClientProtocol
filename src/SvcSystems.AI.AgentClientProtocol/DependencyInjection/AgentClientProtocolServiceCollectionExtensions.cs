using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SvcSystems.AI.AgentClientProtocol.Registry;

namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Registers ACP client services with a <see cref="IServiceCollection"/>.</summary>
public static class AgentClientProtocolServiceCollectionExtensions
{
    /// <summary>Registers an <see cref="IAcpClientFactory"/> and an <see cref="IRegistryClient"/>.</summary>
    /// <remarks>Existing registrations are preserved, so calling this method more than once is safe.</remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="defaultClientOptions">Builds the <see cref="ClientOptions"/> used when callers do not supply
    /// their own. When null, a default instance that reports diagnostics through the container's
    /// <see cref="ILoggerFactory"/> is registered.</param>
    /// <param name="registryIndexUri">Registry index URI. Defaults to <see cref="RegistryClient.DefaultIndexUri"/>.</param>
    /// <returns>The same service collection so that calls can be chained.</returns>
    public static IServiceCollection AddAgentClientProtocol(
        this IServiceCollection services,
        Func<IServiceProvider, ClientOptions>? defaultClientOptions = null,
        Uri? registryIndexUri = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAcpClientFactory>(provider =>
        {
            var options = defaultClientOptions?.Invoke(provider)
                ?? new ClientOptions { LoggerFactory = provider.GetService<ILoggerFactory>() };
            return new AcpClientFactory(options);
        });
        services.TryAddSingleton<IRegistryClient>(provider =>
            new RegistryClient(provider.GetService<HttpClient>(), registryIndexUri));
        return services;
    }
}
