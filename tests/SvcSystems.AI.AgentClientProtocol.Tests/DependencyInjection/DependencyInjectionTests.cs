using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SvcSystems.AI.AgentClientProtocol.Registry;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void RegistersClientFactoryAndRegistryClient()
    {
        var services = new ServiceCollection();
        services.AddAgentClientProtocol();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IAcpClientFactory>());
        Assert.NotNull(provider.GetService<IRegistryClient>());
    }

    [Fact]
    public void RepeatedRegistrationKeepsASingleDescriptor()
    {
        var services = new ServiceCollection();
        services.AddAgentClientProtocol();
        services.AddAgentClientProtocol();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IAcpClientFactory));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IRegistryClient));
    }

    [Fact]
    public void DefaultOptionsUseTheContainerLoggerFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddAgentClientProtocol();
        using var provider = services.BuildServiceProvider();

        var factory = Assert.IsType<AcpClientFactory>(provider.GetRequiredService<IAcpClientFactory>());
        Assert.Same(NullLoggerFactory.Instance, factory.DefaultOptions.LoggerFactory);
    }

    [Fact]
    public void CallerSuppliedOptionsFactoryOverridesTheDefault()
    {
        var expected = new ClientOptions();
        var services = new ServiceCollection();
        services.AddAgentClientProtocol(_ => expected);
        using var provider = services.BuildServiceProvider();

        var factory = Assert.IsType<AcpClientFactory>(provider.GetRequiredService<IAcpClientFactory>());
        Assert.Same(expected, factory.DefaultOptions);
    }

    [Fact]
    public void PreRegisteredRegistryClientIsPreserved()
    {
        using var replacement = new RegistryClient();
        var services = new ServiceCollection();
        services.AddSingleton<IRegistryClient>(replacement);
        services.AddAgentClientProtocol();
        using var provider = services.BuildServiceProvider();

        Assert.Same(replacement, provider.GetRequiredService<IRegistryClient>());
    }
}
