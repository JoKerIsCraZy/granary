using System.Runtime.Versioning;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Accounts;
using Granary.Core.Api.Admin;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Discovery;
using Granary.Core.Api.Events;
using Granary.Core.Api.Images;
using Granary.Core.Api.Notifications;
using Granary.Core.Api.Playback;
using Granary.Core.Settings;
using Granary.Core.ViewModels.Settings;
using Granary.Core.Api.Profiles;
using Granary.Core.Api.Requests;
using Granary.Core.Api.Settings;
using Granary.Core.Api.Subtitles;
using Granary.Core.Api.Theme;
using Granary.Core.Api.WatchParty;
using Granary.Core.Artwork;
using Granary.Core.Auth;
using Granary.Core.Http;
using Granary.Core.Overlays;
using Granary.Core.Playback;
using Granary.Core.Realtime;
using Granary.Core.Security;
using Granary.Core.Servers;
using Granary.Core.Theme;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Admin;
using Granary.Core.ViewModels.Cards;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Granary.Core.WatchParty;

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

        // All Silo API calls. Credentials must never follow a redirect to another origin. The local route, when one
        // is verified, takes them after the credentials were decided by the saved address.
        services.AddTransient<ClientIdentityHandler>();
        services.AddTransient<AuthenticationHandler>();
        services.AddTransient<LocalRouteHandler>();
        services.AddHttpClient<SiloHttp>()
            .ConfigurePrimaryHttpMessageHandler(CreateSocketsHandler)
            .AddHttpMessageHandler<ClientIdentityHandler>()
            .AddHttpMessageHandler<AuthenticationHandler>()
            .AddHttpMessageHandler<LocalRouteHandler>();
        services.AddSingleton<LocalRoutes>();
        services.AddSingleton<ILocalNetwork, LocalNetwork>();
        services.AddSingleton<ILocalRouteProbe, LocalRouteProbe>();
        services.AddSingleton<LocalRouteMonitor>();
        // Servers advertised on this network (_silo._tcp): the app puts Windows' DNS-SD browser in first. A found
        // address is read on a client of its own, with nothing of this device's.
        services.TryAddTransient<ILanServiceBrowser, NoLanServiceBrowser>();
        services.AddHttpClient<ILanServerProbe, LanServerProbe>()
            .ConfigurePrimaryHttpMessageHandler(LanServerProbe.CreateHandler)
            .RemoveAllLoggers();
        services.AddTransient<LanServerDiscovery>();
        services.AddSingleton<LanAdvertWatch>();
        services.AddHttpClient(TokenRefresher.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(CreateSocketsHandler)
            .AddHttpMessageHandler<ClientIdentityHandler>();
        services.AddTransient<AuthApi>();
        services.AddTransient<DeviceLoginApi>();
        services.AddTransient<ProfilesApi>();
        services.AddTransient<SystemApi>();
        services.AddTransient<AccountApi>();
        services.AddTransient<CatalogApi>();
        services.AddTransient<VisibleLibraries>();
        services.AddTransient<WatchApi>();
        services.AddTransient<PlaybackApi>();
        services.AddTransient<PlaybackConnectionApi>();
        AddSpeedTest(services);
        services.AddTransient<TrackPreferencesApi>();
        services.AddTransient<SubtitlesApi>();
        services.AddTransient<MarkersApi>();
        services.AddTransient<AccountPermissions>();
        services.AddTransient<SettingsApi>();
        services.AddTransient<LibraryPageApi>();
        services.AddTransient<CollectionsApi>();
        services.AddTransient<RecommendationsApi>();
        services.AddTransient<CalendarApi>();
        services.AddTransient<NotificationsApi>();
        services.AddTransient<RequestsApi>();
        services.AddTransient<BrandingApi>();
        services.AddTransient<AdminApi>();
        services.AddSingleton<ServerAccent>();
        services.AddSingleton<CardOverlaySettings>();
        services.AddSingleton<CardPresentationSettings>();
        services.AddTransient<ImagesApi>();
        services.AddSingleton<ImageParameterCache>();
        AddArtwork(services);

        services.AddSingleton(_ => ClientIdentity.Load(ClientIdentity.DefaultDeviceIdFile));
        AddPlayback(services);
        AddRealtimeEvents(services);

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
        services.AddSingleton(sp => new SidebarSections(SidebarSections.DefaultPath, sp.GetRequiredService<ILogger<SidebarSections>>()));
        services.AddSingleton(sp => new SidebarLayout(SidebarLayout.DefaultPath, sp.GetRequiredService<ISessionManager>(),
            sp.GetRequiredService<IUiDispatcher>(), sp.GetRequiredService<ILogger<SidebarLayout>>()));
        services.AddSingleton(sp => new LibraryTabs(LibraryTabs.DefaultPath, sp.GetRequiredService<ILogger<LibraryTabs>>()));
        // The app brings its own updater (Velopack); without it nothing updates.
        services.TryAddSingleton<IAppUpdater, NoAppUpdater>();
        services.AddSingleton<AppUpdatesViewModel>();
        services.AddTransient<SignInViewModel>();
        services.AddSingleton<MediaCardActions>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<SectionViewModel>();
        services.AddTransient<SearchViewModel>();
        services.AddTransient<RequestToAddViewModel>();
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<PersonalListViewModel>();
        services.AddTransient<ViewModels.Collections.CollectionEditorViewModel>();
        services.AddTransient<ViewModels.Collections.AddToCollectionViewModel>();
        services.AddTransient<WatchlistTitlesViewModel>();
        services.AddTransient<CollectionsViewModel>();
        services.AddTransient<ForYouViewModel>();
        services.AddTransient<RecommendationSectionViewModel>();
        services.AddTransient<PersonViewModel>();
        services.AddTransient<CalendarViewModel>();
        services.AddSingleton<NotificationBadge>();
        services.AddTransient<NotificationsViewModel>();
        services.AddSingleton<RequestsFeature>();
        services.AddSingleton<WatchPartyFeature>();
        services.AddTransient<Granary.Core.ViewModels.WatchParty.WatchPartyHubViewModel>();
        services.AddTransient<Granary.Core.ViewModels.WatchParty.WatchPartyRoomViewModel>();
        services.AddTransient<RequestsViewModel>();
        services.AddTransient<TitleViewModel>();
        services.AddTransient<RequestTitlesViewModel>();
        services.AddTransient<AddServerViewModel>();
        services.AddTransient<PlayerViewModel>();
        services.AddTransient<Granary.Core.ViewModels.Player.AddSubtitlesViewModel>();
        services.AddTransient<ItemDetailViewModel>();
        services.AddTransient<IProfileSettings, ProfileSettings>();
        services.AddTransient<PlaybackSettingsViewModel>();
        services.AddTransient<LanguageSettingsViewModel>();
        services.AddSingleton<AdminSidebarViewModel>();
        services.AddTransient<AdminDashboardViewModel>();
        services.AddTransient<AdminActivityViewModel>();
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
        // Each playback keeps its session's control lane: an administrator's pause, stop or message reaches the player.
        // Made outside the container, as the playback is: a transient disposable resolved from the root provider would be
        // kept until the app exits.
        services.AddTransient<PlaybackControlApi>();
        services.AddSingleton<Func<IRealtimeSocket>>(() => new WebSocketRealtimeSocket());
        services.AddSingleton<Func<PlaybackControlLane>>(sp => () => new PlaybackControlLane(sp.GetRequiredService<PlaybackControlApi>(),
            sp.GetRequiredService<Func<IRealtimeSocket>>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<PlaybackControlLane>>()));
        services.AddSingleton<PlaybackStops>();
        services.AddSingleton<ViewerStateChanges>();
        // The Watch Party this profile is in, for the room page and the player alike; its socket is made per engagement.
        services.AddTransient<WatchPartyApi>();
        services.AddSingleton<Func<WatchPartyConnection>>(sp => () => new WatchPartyConnection(sp.GetRequiredService<WatchPartyApi>(),
            sp.GetRequiredService<Func<IRealtimeSocket>>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<WatchPartyConnection>>()));
        services.AddSingleton<WatchPartyRecentRooms>();
        services.AddSingleton<WatchPartySession>();
        // Media of the saved address takes its local route too.
        services.AddSingleton<MediaProxy>(sp => new MediaProxy(
            new LocalRouteHandler(sp.GetRequiredService<LocalRoutes>(), sp.GetRequiredService<ILogger<LocalRouteHandler>>()) { InnerHandler = CreateMediaHandler() },
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<MediaProxy>>()));
        services.AddSingleton<IMediaProxy>(sp => sp.GetRequiredService<MediaProxy>());
        services.AddSingleton<Func<IServerPlayback>>(sp => () => ActivatorUtilities.CreateInstance<ServerPlayback>(sp));
        services.AddTransient<IUpNextSource, UpNextSource>();
        // The episodes that started by themselves count across player pages, as long as the app runs.
        services.AddSingleton<AutoPlayStreak>();
    }

    // The server's events for the signed-in profile (docs/api-contract.md, 12): one connection for as long as the app runs.
    // Its lanes are made outside the container, as the playback control lanes are.
    private static void AddRealtimeEvents(IServiceCollection services)
    {
        services.AddTransient<EventsApi>();
        services.AddSingleton<Func<EventsLane>>(sp => () => new EventsLane(sp.GetRequiredService<EventsApi>(),
            () => new WebSocketRealtimeSocket(EventsLane.MaxFrameBytes), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<EventsLane>>()));
        services.AddSingleton<RealtimeEvents>();
    }

    // Settings → Test speed. A run outlives the page that started it, so a second press joins it. The probe may redirect
    // to a short-lived proxy address with a token of its own: that hop goes without credentials and stays out of the log.
    private static void AddSpeedTest(IServiceCollection services)
    {
        services.AddHttpClient(BandwidthProbeApi.HopClientName)
            .ConfigurePrimaryHttpMessageHandler(CreateSocketsHandler)
            .RemoveAllLoggers();
        services.AddTransient<BandwidthProbeApi>();
        services.AddTransient<BandwidthProbe>();
        services.AddSingleton<ConnectionSpeedTest>();
        // The app puts the system's view of the network in first.
        services.TryAddSingleton<INetworkLink, UnknownNetworkLink>();
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
