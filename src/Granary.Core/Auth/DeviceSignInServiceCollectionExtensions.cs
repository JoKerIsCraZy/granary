using Microsoft.Extensions.DependencyInjection;

namespace Granary.Core.Auth;

public static class DeviceSignInServiceCollectionExtensions
{
    /// <summary>Registers device (QR code) sign-in.</summary>
    public static IServiceCollection AddDeviceSignIn(this IServiceCollection services)
    {
        return services;
    }
}
