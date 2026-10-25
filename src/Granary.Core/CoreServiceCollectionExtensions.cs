using System.Runtime.Versioning;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Accounts;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Discovery;
using Granary.Core.Api.Images;
using Granary.Core.Api.Playback;
using Granary.Core.Api.Profiles;
using Granary.Core.Api.Settings;
using Granary.Core.Artwork;
using Granary.Core.Auth;
using Granary.Core.Http;
using Granary.Core.Playback;
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
        services.AddTransient<WatchApi>();
        services.AddTransient<PlaybackApi>();
        services.AddTransient<SettingsApi>();
        services.AddTransient<LibraryPageApi>();
        services.AddTransient<ImagesApi>();
        services.AddSingleton<ImageParameterCache>();
        AddArtwork(services);

        services.AddSingleton(_ => ClientIdentity.Load(ClientIdentity.DefaultDeviceIdFile));
        AddPlayback(services);

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
        services.AddSingleton<SidebarViewModel>();
        services.AddTransient<SignInViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<SectionViewModel>();
        services.AddTransient<SearchViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<AddServerViewModel>();
        services.AddTransient<PlayerViewModel>();
        services.AddTransient<ItemDetailViewModel>();
        return services;
    }

    // A server playback per start of the player; the device probe (IPlaybackDeviceProbe) comes from the player layer.
    // The player disposes its playbacks itself: made outside the container, they are not kept alive until the app
    // exits, as transient disposables resolved from the root provider would be.
    private static void AddPlayback(IServiceCollection services)
    {
        services.AddTransient<PlaybackPreferencesReader>();
        services.AddSingleton<IMediaAuthorization, MediaAuthorization>();
        services.AddTransient<PlaybackStarter>();
        services.AddSingleton<PlaybackStops>();
        services.AddSingleton<MediaProxy>(sp => new MediaProxy(CreateMediaHandler(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<MediaProxy>>()));
        services.AddSingleton<IMediaProxy>(sp => sp.GetRequiredService<MediaProxy>());
        services.AddSingleton<Func<IServerPlayback>>(sp => () => ActivatorUtilities.CreateInstance<ServerPlayback>(sp));
    }

    // Artwork URLs are presigned and public: no credentials, no identity headers. HTTP/2 lets a page's images
    // share one connection, and their requests stay out of the log, where a page would add hundreds.
    private static void AddArtwork(IServiceCollection services)
    {
        // The loader times each download itself, body included (ArtworkLoaderOptions.DownloadTimeout). HTTP/2
        // pings notice a connection that died silently, as a NAS restart leaves it.
        services.AddHttpClient(ArtworkLoader.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = TimeSpan.FromSeconds(20),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                MaxAutomaticRedirections = 3,
            })
            .RemoveAllLoggers();
        services.AddSingleton(sp => new ArtworkDiskCache(
            ArtworkDiskCache.DefaultDirectory,
            ArtworkDiskCache.DefaultCapacityBytes,
            sp.GetRequiredService<ILogger<ArtworkDiskCache>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ArtworkLoader>();
        services.AddSingleton<IArtworkPrefetcher>(sp => sp.GetRequiredService<ArtworkLoader>());
    }

    // The media proxy's way upstream: a stream can run for hours, so no timeout but the player's own, which ends the
    // request when it gives up. No redirects, cookies or decompression: the bytes go to mpv as the server sent them.
    private static SocketsHttpHandler CreateMediaHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    // Long-lived services keep their typed clients, so connections are recycled to follow DNS changes.
    private static SocketsHttpHandler CreateSocketsHandler() =>
        new() { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    [SupportedOSPlatform("windows")]
    private static void AddWindowsSecretStore(IServiceCollection services) =>
        services.AddSingleton<ISecretStore>(sp =>
            new DpapiSecretStore(DpapiSecretStore.DefaultPath, sp.GetRequiredService<ILogger<DpapiSecretStore>>()));
}
