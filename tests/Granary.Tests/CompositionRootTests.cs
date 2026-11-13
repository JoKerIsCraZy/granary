using Granary.Core;
using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Artwork;
using Granary.Core.Auth;
using Granary.Core.Downloads;
using Granary.Core.Playback;
using Granary.Core.Realtime;
using Granary.Core.Servers;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Downloads;
using Granary.Core.ViewModels.Settings;
using Granary.Player;
using Granary.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Granary.Tests;

public sealed class CompositionRootTests
{
    [Fact]
    public void Services_ResolveAndTheEngineIsShared()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll")),
            "libmpv-2.dll not found; run scripts/fetch-native.ps1.");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IUiDispatcher, ImmediateDispatcher>();
        services.AddSingleton<INavigationService, RecordingNavigation>();
        services.AddSingleton<IConfirmation, FakeConfirmation>();
        services.AddSingleton<IFileLauncher, RecordingFileLauncher>();
        services.AddSingleton<IDownloadOptionsDialog, FakeDownloadOptionsDialog>();
        services.AddSingleton<IFolderPicker, FakeFolderPicker>();
        services.AddGranaryCore();
        services.AddGranaryPlayer(new MpvEngineOptions { VideoOutput = VideoOutputKind.None });

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<IServerProbe>());
        Assert.NotNull(provider.GetRequiredService<ISecretStore>());
        Assert.NotNull(provider.GetRequiredService<AddServerViewModel>());
        Assert.NotNull(provider.GetRequiredService<ServersViewModel>());
        Assert.NotNull(provider.GetRequiredService<SignInViewModel>());
        Assert.NotNull(provider.GetRequiredService<HomeViewModel>());
        Assert.Same(provider.GetRequiredService<ShellViewModel>(), provider.GetRequiredService<ShellViewModel>());
        Assert.IsType<SessionManager>(provider.GetRequiredService<ISessionManager>());
        Assert.NotNull(provider.GetRequiredService<SiloHttp>());
        Assert.Same(provider.GetRequiredService<ArtworkLoader>(), provider.GetRequiredService<IArtworkPrefetcher>());
        Assert.NotNull(provider.GetRequiredService<ItemDetailViewModel>());
        Assert.IsType<MpvDeviceProbe>(provider.GetRequiredService<IPlaybackDeviceProbe>());
        Assert.IsType<ServerPlayback>(provider.GetRequiredService<Func<IServerPlayback>>()());
        Assert.Same(provider.GetRequiredService<RealtimeEvents>(), provider.GetRequiredService<RealtimeEvents>());
        // The Discord status's TMDB answers are kept for the run, across player pages.
        Assert.Same(provider.GetRequiredService<TmdbTitleLookup>(), provider.GetRequiredService<TmdbTitleLookup>());
        Assert.NotSame(provider.GetRequiredService<Func<EventsLane>>()(), provider.GetRequiredService<Func<EventsLane>>()());
        using var player = provider.GetRequiredService<PlayerViewModel>();
        Assert.Same(provider.GetRequiredService<IPlaybackEngine>(), provider.GetRequiredService<ICompositionVideoOutput>());
        // Made by their views, so nothing else checks what they ask for.
        Assert.NotNull(ActivatorUtilities.CreateInstance<MpvConfigEditorViewModel>(provider));
        using var presets = ActivatorUtilities.CreateInstance<MpvSettingsViewModel>(provider);
        using var buttons = ActivatorUtilities.CreateInstance<PlayerButtonsSettingsViewModel>(provider);
        // The activity indicator's switch: one for its settings group, the events stream and the indicator.
        using var indicatorSwitch = ActivatorUtilities.CreateInstance<ActivityIndicatorSettingsViewModel>(provider);
        Assert.Same(provider.GetRequiredService<ActivityIndicatorSetting>(), provider.GetRequiredService<ActivityIndicatorSetting>());
        Assert.Same(provider.GetRequiredService<PlayerButtonsLayout>(), provider.GetRequiredService<IPlayerButtonsLayout>());
        // One queue runs every transfer, and the pages and the title pages share one manager.
        Assert.Same(provider.GetRequiredService<DownloadQueue>(), provider.GetRequiredService<DownloadQueue>());
        Assert.Same(provider.GetRequiredService<DownloadManager>(), provider.GetRequiredService<DownloadManager>());
        // The Downloads page and Settings → Downloads make theirs outside the container, which would keep a disposable one.
        using var downloads = ActivatorUtilities.CreateInstance<DownloadsViewModel>(provider);
        Assert.NotNull(ActivatorUtilities.CreateInstance<DownloadSettingsViewModel>(provider));
        // A title on this PC plays from it, and the next episode can come from this PC too.
        Assert.Same(provider.GetRequiredService<DownloadPlaybacks>(), provider.GetRequiredService<DownloadPlaybacks>());
        Assert.IsType<DownloadUpNext>(provider.GetRequiredService<IUpNextSource>());
        // One queue keeps what was watched from this PC, and one sync sends it.
        Assert.Same(provider.GetRequiredService<OfflineProgressQueue>(), provider.GetRequiredService<OfflineProgressQueue>());
        Assert.Same(provider.GetRequiredService<OfflineProgressSync>(), provider.GetRequiredService<OfflineProgressSync>());
        // The shell asks at the start whether the server is there, and the Downloads page shows what it found.
        Assert.Same(provider.GetRequiredService<OfflineStart>(), provider.GetRequiredService<OfflineStart>());
        // Every card's Download entries come from one place for the app, which the cards ask as their menus open.
        Assert.Same(provider.GetRequiredService<CardDownloads>(), provider.GetRequiredService<CardDownloads>());
        var record = new DownloadRecord
        {
            DownloadId = "dl_1", Scope = new DownloadScope("srv1", "1", "7"), MediaItemId = "mv_1", Title = "Example Movie", Quality = "original",
            State = DownloadState.Completed, MediaFile = "media.mkv",
        };
        var media = new LocalMedia(new StoredDownload(record, Path.Combine(Path.GetTempPath(), "granary-tests", "none"), FilesMissing: false),
            Granary.Core.Api.Downloads.OfflineManifest.Read("""{ "download_id": "dl_1", "content_id": "mv_1" }"""u8), new HashSet<string>());
        Assert.NotNull(provider.GetRequiredService<Func<LocalMedia, LocalPlayback>>()(media));
    }

    [Fact]
    public void WithoutTheAppsDownloadDialog_TheServicesStillResolve()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll")),
            "libmpv-2.dll not found; run scripts/fetch-native.ps1.");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IUiDispatcher, ImmediateDispatcher>();
        services.AddSingleton<INavigationService, RecordingNavigation>();
        services.AddSingleton<IConfirmation, FakeConfirmation>();
        services.AddSingleton<IFileLauncher, RecordingFileLauncher>();
        services.AddGranaryCore();
        services.AddGranaryPlayer(new MpvEngineOptions { VideoOutput = VideoOutputKind.None });

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<DownloadActions>());
        Assert.IsType<NoDownloadOptionsDialog>(provider.GetRequiredService<IDownloadOptionsDialog>());
        // The sign-out paths' view models take it, so a sign-out asks about the downloads and takes them along.
        Assert.Same(provider.GetRequiredService<DownloadSignOut>(), provider.GetRequiredService<DownloadSignOut>());
    }

    [Fact]
    public void SiloApiException_ExposesTheProblemWithoutItsDetail()
    {
        var problem = new ApiProblem($"{ApiProblem.TypeOrigin}{ProblemCodes.RateLimited}", "Rate limited", 429,
            "Bearer abc is not allowed", "urn:silo:request:1", null);

        var exception = new SiloApiException(429, problem, TimeSpan.FromSeconds(5));

        Assert.Equal(ProblemCodes.RateLimited, exception.Code);
        Assert.Equal(TimeSpan.FromSeconds(5), exception.RetryAfter);
        Assert.Equal("Silo API returned 429 rate_limited", exception.Message);
        // A problem the server wrote names its type; a status alone can be anyone's answer.
        Assert.True(exception.HasProblem);
        Assert.False(new SiloApiException(403, ApiProblem.FromStatus(403)).HasProblem);
    }
}
