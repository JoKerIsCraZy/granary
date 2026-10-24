using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Granary.Core.Auth;

public static class DeviceSignInServiceCollectionExtensions
{
    /// <summary>Registers device (QR code) sign-in.</summary>
    public static IServiceCollection AddDeviceSignIn(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddTransient<DeviceSignInFlow>();
        services.AddTransient<DeviceSignInViewModel>();
        return services;
    }
}
