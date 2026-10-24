using System.Runtime.Versioning;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Accounts;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Discovery;
using Granary.Core.Api.Profiles;
using Granary.Core.Auth;
using Granary.Core.Security;
using Granary.Core.Servers;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Granary.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>Registers the UI-independent services and view models.</summary>
    public static IServiceCollection AddGranaryCore(this IServiceCollection services)
    {
        // The probe talks to servers the user has not signed in to: no credentials, no redirects.
        services.AddHttpClient<IServerProbe, ServerProbe>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        // All Silo API calls. Credentials must never follow a redirect to another origin.
        services.AddHttpClient<SiloHttp>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddTransient<AuthApi>();
        services.AddTransient<DeviceLoginApi>();
        services.AddTransient<ProfilesApi>();
        services.AddTransient<SystemApi>();
        services.AddTransient<AccountApi>();

        services.AddSingleton(_ => ClientIdentity.Load(ClientIdentity.DefaultDeviceIdFile));

        if (OperatingSystem.IsWindows())
        {
            AddWindowsSecretStore(services);
        }

        services.AddServerManagement();
        services.AddDeviceSignIn();
        services.AddProfiles();

        services.AddTransient<AddServerViewModel>();
        services.AddTransient<PlayerViewModel>();
        return services;
    }

    [SupportedOSPlatform("windows")]
    private static void AddWindowsSecretStore(IServiceCollection services) =>
        services.AddSingleton<ISecretStore>(_ => new DpapiSecretStore(DpapiSecretStore.DefaultPath));
}
