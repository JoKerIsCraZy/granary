using Granary.Core;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Api.Requests;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static Granary.Tests.Browse.BrowsePayloads;

namespace Granary.Tests.Browse;

/// <summary>
/// Settings → Navigation &amp; Cards → Title preview at the top: off, Home, For You and Requests' Discover start with their
/// rows, and a card pointed at changes nothing; Home's banner, which a row marked on the web brings, still shows. A change
/// made while a page is away applies as it shows again.
/// </summary>
public sealed class BrowseHeroSwitchTests : IDisposable
{
    private const string SectionsPath = "api/v2/home/sections";
    private const string RecommendationsPath = "api/v2/recommendations/discover";
    private const string RequestsDiscover = """
        { "items": [ { "key": "trending", "title": "Trending", "page": 1, "total_pages": 1, "total_results": 2, "results": [
          { "tmdb_id": 10, "media_type": "movie", "title": "New One", "year": 2026, "availability": "none", "request": { "requestable": true } },
          { "tmdb_id": 11, "media_type": "movie", "title": "Next One", "year": 2026, "availability": "none", "request": { "requestable": true } }
        ] } ] }
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granary-tests", Guid.NewGuid().ToString("N"));
    // Home's server.
    private readonly BrowseFakeSilo _silo = new();
    private readonly ManualTimeProvider _time = new();
    private readonly FakeSessionManager _session = BrowseFakeSilo.ReadySession();
    // For You's and Requests' server.
    private readonly LibraryHarness _harness = new();

    public BrowseHeroSwitchTests()
    {
        _harness.Answer(RecommendationsPath, RecommendationPayloads.Discover);
        _harness.Answer("api/v2/recommendations/taste-profile", RecommendationPayloads.Taste);
        _harness.Answer("api/v2/requests/mine", """{ "items": [] }""");
        _harness.Answer("api/v2/requests/discover", RequestsDiscover);
        foreach (string brands in new[] { "studios", "networks", "genres" })
        {
            _harness.Answer($"api/v2/requests/discover/{brands}", """{ "items": [] }""");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private BrowseHeroSetting SettingFor(FakeSessionManager session) =>
        new(Path.Combine(_directory, "browse-hero.json"), session, new ImmediateDispatcher(), _silo.Logs.For<BrowseHeroSetting>());

    private async Task<BrowseHeroSetting> TurnedOffAsync(FakeSessionManager session)
    {
        var setting = SettingFor(session);
        await setting.SetShownAsync(false);
        return setting;
    }

    private async Task<HomeViewModel> HomeAsync(BrowseHeroSetting setting, params object[] sections)
    {
        _silo.Map(SectionsPath, Sections(sections));
        var home = new HomeViewModel(_session, _silo.Catalog, _silo.Images, new RecordingNavigation(), new ImmediateDispatcher(), _time,
            _silo.Logs.For<HomeViewModel>(), new ViewerStateChanges(new PlaybackStops()), browseHero: setting);
        await home.ActivateAsync();
        return home;
    }

    private async Task<ForYouViewModel> ForYouAsync(BrowseHeroSetting setting)
    {
        var page = new ForYouViewModel(_harness.Session, new RecommendationsApi(_harness.Silo.Http),
            new ImageParameterCache(new ImagesApi(_harness.Silo.Http), _harness.Logs.For<ImageParameterCache>()), _harness.Navigation,
            new ImmediateDispatcher(), _harness.Clock, _harness.Logs.For<ForYouViewModel>(), browseHero: setting);
        await page.ActivateAsync(isReturn: false);
        return page;
    }

    private async Task<RequestsViewModel> RequestsAsync(BrowseHeroSetting setting)
    {
        var hub = new RequestsViewModel(_harness.Session, new RequestsApi(_harness.Silo.Http), _harness.Navigation, _harness.Clock,
            new ImmediateDispatcher(), _harness.Logs.For<RequestsViewModel>(), browseHero: setting);
        await hub.ActivateAsync(isReturn: false);
        return hub;
    }

    [Fact]
    public async Task Home_WithTheTitlePreviewOff_ShowsNoHero_AndACardPointedAtStartsNoTimer()
    {
        var home = await HomeAsync(await TurnedOffAsync(_session), Section("recent", "recently_added", 2, Item("r1"), Item("r2")));

        Assert.Null(home.HeroTitle);
        home.PointCardCommand.Execute(home.Rows[0].Cards[0]);
        home.PointCardCommand.Execute(home.Rows[0].Cards[1]);

        Assert.Null(home.HeroTitle);
        Assert.Null(_time.NextDue);
    }

    [Fact]
    public async Task Home_WithTheTitlePreviewOff_StillShowsTheBannerOfARowMarkedOnTheWeb()
    {
        var home = await HomeAsync(await TurnedOffAsync(_session),
            Featured("picks", "collection", 2, Item("f1"), Item("f2")), Section("recent", "recently_added", 1, Item("r1")));

        Assert.True(home.Banner.HasSlides);
        Assert.Equal("f1", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task Home_TurnedOffWhileAway_StartsWithItsRowsWhenItShowsAgain_AndTurnedOnShowsTheFirstCardAgain()
    {
        var setting = SettingFor(_session);
        var home = await HomeAsync(setting, Section("recent", "recently_added", 2, Item("r1"), Item("r2")));
        home.PointCardCommand.Execute(home.Rows[0].Cards[1]);
        home.Deactivate();

        await setting.SetShownAsync(false);
        await home.ActivateAsync();
        Assert.Null(home.HeroTitle);

        home.Deactivate();
        await setting.SetShownAsync(true);
        await home.ActivateAsync();
        Assert.Equal("r1", home.HeroTitle?.Key);
        // The first card pointed at shows at once again.
        home.PointCardCommand.Execute(home.Rows[0].Cards[1]);
        Assert.Equal("r2", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task Home_TurnedOff_TellsThePageTheHeroWent_SoItsRowsMoveUp()
    {
        var setting = SettingFor(_session);
        var home = await HomeAsync(setting, Section("recent", "recently_added", 1, Item("r1")));
        Assert.True(home.HasHeroTitle);
        home.Deactivate();
        var changed = new List<string?>();
        home.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await setting.SetShownAsync(false);
        await home.ActivateAsync();

        Assert.False(home.HasHeroTitle);
        Assert.Contains(nameof(HomeViewModel.HasHeroTitle), changed);
    }

    [Fact]
    public async Task Home_TurnedOffBeforeAPointedCardSettled_StartsWithItsRowsOnItsReturn_AndTurnedOnShowsTheFirstCardPointedAtAtOnce()
    {
        var setting = SettingFor(_session);
        var home = await HomeAsync(setting, Section("recent", "recently_added", 3, Item("r1"), Item("r2"), Item("r3")));
        home.PointCardCommand.Execute(home.Rows[0].Cards[0]);
        home.PointCardCommand.Execute(home.Rows[0].Cards[1]);
        home.Deactivate();

        await setting.SetShownAsync(false);
        _time.Advance(BrowseHeroViewModel.SettleTime);

        // The card waiting to settle does not show, and Home has still to take its rows without the title preview.
        Assert.NotEqual("r2", home.Hero.Shown?.Key);
        Assert.True(home.Hero.MissedSwitch);

        await home.ActivateAsync();
        Assert.Null(home.Hero.Shown);
        Assert.False(home.Hero.MissedSwitch);

        home.Deactivate();
        await setting.SetShownAsync(true);
        await home.ActivateAsync();
        home.PointCardCommand.Execute(home.Rows[0].Cards[2]);
        Assert.Equal("r3", home.Hero.Shown?.Key);
    }

    [Fact]
    public async Task FromTheContainer_HomeForYouAndDiscover_FollowTheSwitch()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IUiDispatcher, ImmediateDispatcher>();
        services.AddSingleton<INavigationService, RecordingNavigation>();
        services.AddSingleton<IConfirmation, FakeConfirmation>();
        services.AddGranaryCore();
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(BrowseHeroSetting)).Lifetime);
        // A profile acting, and the switch's file in this test's own folder, never in the data directory.
        services.Replace(ServiceDescriptor.Singleton<ISessionManager>(_session));
        services.Replace(ServiceDescriptor.Singleton(_ => SettingFor(_session)));
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<BrowseHeroSetting>().SetShownAsync(false);

        Assert.False(provider.GetRequiredService<HomeViewModel>().Hero.IsOn);
        Assert.False(provider.GetRequiredService<ForYouViewModel>().Hero.IsOn);
        Assert.False(provider.GetRequiredService<RequestsViewModel>().Hero.IsOn);
    }

    [Fact]
    public async Task Home_AnotherProfile_FollowsItsOwnSwitch()
    {
        var home = await HomeAsync(await TurnedOffAsync(_session), Section("recent", "recently_added", 1, Item("r1")));
        Assert.Null(home.HeroTitle);

        _session.ProfileId = "8";
        _session.RaiseChanged();
        await home.Loading;

        Assert.Equal("r1", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task ForYou_WithTheTitlePreviewOff_ShowsNoHero_AndACardPointedAtChangesNothing()
    {
        var page = await ForYouAsync(await TurnedOffAsync(_harness.Session));

        page.Rows[0].PointCardCommand!.Execute(page.Rows[0].Cards[1]);
        _harness.Clock.Advance(BrowseHeroViewModel.SettleTime);

        Assert.NotEmpty(page.Rows);
        Assert.Null(page.Hero.Shown);
    }

    [Fact]
    public async Task ForYou_TurnedOffWhileAway_LosesItsHeroOnTheWayBack_AndTurnedOnShowsTheFirstCardAgain()
    {
        var setting = SettingFor(_harness.Session);
        var page = await ForYouAsync(setting);
        Assert.Equal("movie:heat-1995", page.Hero.Shown?.Key);

        await setting.SetShownAsync(false);
        await page.ActivateAsync(isReturn: true);
        Assert.Null(page.Hero.Shown);

        await setting.SetShownAsync(true);
        await page.ActivateAsync(isReturn: true);
        Assert.Equal("movie:heat-1995", page.Hero.Shown?.Key);
    }

    [Fact]
    public async Task ForYou_TurnedOff_TellsThePageTheHeroWent_SoItsRowsMoveUp()
    {
        var setting = SettingFor(_harness.Session);
        var page = await ForYouAsync(setting);
        Assert.True(page.Hero.HasTitle);
        var changed = new List<string?>();
        page.Hero.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await setting.SetShownAsync(false);
        await page.ActivateAsync(isReturn: true);

        Assert.False(page.Hero.HasTitle);
        Assert.Contains(nameof(BrowseHeroViewModel.HasTitle), changed);
    }

    [Fact]
    public async Task RequestsDiscover_TurnedOff_TellsThePageTheHeroWent_SoItsRowsMoveUp()
    {
        var setting = SettingFor(_harness.Session);
        var hub = await RequestsAsync(setting);
        Assert.Equal("New One", hub.HeroTitle?.Heading);
        Assert.True(hub.HasHeroTitle);
        hub.Deactivate();
        var changed = new List<string?>();
        hub.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await setting.SetShownAsync(false);
        await hub.ActivateAsync(isReturn: true);

        Assert.Null(hub.HeroTitle);
        Assert.False(hub.HasHeroTitle);
        Assert.Contains(nameof(RequestsViewModel.HeroTitle), changed);
        Assert.Contains(nameof(RequestsViewModel.HasHeroTitle), changed);
    }

    [Fact]
    public async Task RequestsDiscover_WithTheTitlePreviewOff_ShowsNoHero_AndACardPointedAtChangesNothing()
    {
        var hub = await RequestsAsync(await TurnedOffAsync(_harness.Session));

        hub.DiscoverRows[0].PointCardCommand!.Execute(hub.DiscoverRows[0].Cards[1]);
        _harness.Clock.Advance(BrowseHeroViewModel.SettleTime);

        Assert.NotEmpty(hub.DiscoverRows);
        Assert.Null(hub.Hero.Shown);
    }

    [Fact]
    public async Task RequestsDiscover_TurnedOffWhileAway_LosesItsHeroOnTheWayBack_AndTurnedOnShowsTheFirstCardAgain()
    {
        var setting = SettingFor(_harness.Session);
        var hub = await RequestsAsync(setting);
        Assert.Equal("New One", hub.Hero.Shown?.Heading);
        hub.Deactivate();

        await setting.SetShownAsync(false);
        await hub.ActivateAsync(isReturn: true);
        Assert.Null(hub.Hero.Shown);

        hub.Deactivate();
        await setting.SetShownAsync(true);
        await hub.ActivateAsync(isReturn: true);
        Assert.Equal("New One", hub.Hero.Shown?.Heading);
    }
}
