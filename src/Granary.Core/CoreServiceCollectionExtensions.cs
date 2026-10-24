using System.Runtime.Versioning;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Accounts;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Discovery;
using Granary.Core.Api.Images;
using Granary.Core.Api.Profiles;
using Granary.Core.Auth;
using Granary.Core.Http;
using Granary.Core.Security;
using Granary.Core.Servers;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Granary.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>Registers the UI-independent services and view models.</summary>
    public static IServiceCollection AddGranaryCore(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        // The probe talks to servers the user has not signed in to: no credentials, no redirects.
        services.AddHttpClient<IServerProbe, ServerProbe>()
            .ConfigurePrimaryHttpMessageHandler(CreateSocketsHandler);

        // All Silo API calls. Credentials must never follow a redirect to another origin.
        services.AddTransient<ClientIdentityHandler>();
        services.AddTransient<AuthenticationHandler>();
        services.AddHttpClient<SiloHttp>()
            .ConfigurePrimaryHttpMessageHandler(CreateSocketsHandler)
            .AddHttpMessageHandler<ClientIdentityHandler>()
            .AddHttpMessageHandler<AuthenticationHandler>();
        services.AddHttpClient(TokenRefresher.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(CreateSocketsHandler)
            .AddHttpMessageHandler<ClientIdentityHandler>();
        services.AddTransient<AuthApi>();
        services.AddTransient<DeviceLoginApi>();
        services.AddTransient<ProfilesApi>();
        services.AddTransient<SystemApi>();
        services.AddTransient<AccountApi>();
        services.AddTransient<CatalogApi>();
        services.AddTransient<ImagesApi>();

        services.AddSingleton(_ => ClientIdentity.Load(ClientIdentity.DefaultDeviceIdFile));

        if (OperatingSystem.IsWindows())
        {
            AddWindowsSecretStore(services);
        }

        services.AddSingleton<ICredentialStore, CredentialStore>();
        services.AddSingleton<TokenRefresher>();
        services.AddSingleton<ISessionManager, SessionManager>();

        services.AddServerManagement();
        services.AddDeviceSignIn();
        services.AddProfiles();

        services.AddSingleton<ShellViewModel>();
        services.AddTransient<SignInViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<AddServerViewModel>();
        services.AddTransient<PlayerViewModel>();
        return services;
    }

    // Long-lived services keep their typed clients, so connections are recycled to follow DNS changes.
    private static SocketsHttpHandler CreateSocketsHandler() =>
        new() { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    [SupportedOSPlatform("windows")]
    private static void AddWindowsSecretStore(IServiceCollection services) =>
        services.AddSingleton<ISecretStore>(sp =>
            new DpapiSecretStore(DpapiSecretStore.DefaultPath, sp.GetRequiredService<ILogger<DpapiSecretStore>>()));
}
