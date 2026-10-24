using System.Runtime.Versioning;
using Granary.Core.Abstractions;
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

        if (OperatingSystem.IsWindows())
        {
            AddWindowsSecretStore(services);
        }

        services.AddTransient<AddServerViewModel>();
        services.AddTransient<PlayerViewModel>();
        return services;
    }

    [SupportedOSPlatform("windows")]
    private static void AddWindowsSecretStore(IServiceCollection services) =>
        services.AddSingleton<ISecretStore>(_ => new DpapiSecretStore(DpapiSecretStore.DefaultPath));
}
