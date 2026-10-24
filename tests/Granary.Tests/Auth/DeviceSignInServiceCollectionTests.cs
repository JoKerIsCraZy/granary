using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Auth;
using Granary.Core.Auth;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Granary.Tests.Auth;

public sealed class DeviceSignInServiceCollectionTests
{
    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient<SiloHttp>();
        services.AddTransient<DeviceLoginApi>();
        services.AddSingleton(DeviceSignInFakeServer.Identity);
        services.AddSingleton<ISessionManager, FakeSessionManager>();
        services.AddSingleton<IUiDispatcher, ImmediateDispatcher>();
        return services;
    }

    [Fact]
    public async Task AddDeviceSignIn_RegistersTransientFlowsAndViewModels()
    {
        var services = Services();
        services.AddDeviceSignIn();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Assert.NotSame(provider.GetRequiredService<DeviceSignInFlow>(), provider.GetRequiredService<DeviceSignInFlow>());
        Assert.NotSame(provider.GetRequiredService<DeviceSignInViewModel>(), provider.GetRequiredService<DeviceSignInViewModel>());
    }

    [Fact]
    public async Task AddDeviceSignIn_KeepsAnExistingTimeProvider()
    {
        var time = new ManualTimeProvider();
        var services = Services();
        services.AddSingleton<TimeProvider>(time);
        services.AddDeviceSignIn();

        await using var provider = services.BuildServiceProvider();

        Assert.Same(time, provider.GetRequiredService<TimeProvider>());
    }
}
