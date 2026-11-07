using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using static Granary.Tests.Browse.BrowsePayloads;

namespace Granary.Tests.Browse;

/// <summary>Home's banner: the row the server marks as the hero, as the web's <c>HeroBanner</c>.</summary>
public sealed class HomeBannerTests
{
    private const string SectionsPath = "api/v2/home/sections";
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);

    private readonly BrowseFakeSilo _silo = new();
    private readonly ManualTimeProvider _time = new();
    private readonly FakeSessionManager _session = BrowseFakeSilo.ReadySession();
    private readonly RecordingNavigation _navigation = new();
    private readonly RecordingArtworkPrefetcher _artwork = new();
    private readonly ViewerStateChanges _changes = new(new PlaybackStops());

    private async Task<HomeViewModel> HomeWithAsync(params object[] sections)
    {
        _silo.Map(SectionsPath, Sections(sections));
        var home = new HomeViewModel(_session, _silo.Catalog, _silo.Images, _navigation, new ImmediateDispatcher(), _time,
            _silo.Logs.For<HomeViewModel>(), _changes, _artwork);
        await home.ActivateAsync();
        return home;
    }

    private async Task ReloadAsync(HomeViewModel home, params object[] sections)
    {
        _silo.Map(SectionsPath, Sections(sections));
        _changes.Report();
        await home.Loading;
    }

    private Task<HomeViewModel> HomeWithThreeSlidesAsync() =>
        HomeWithAsync(Featured("picks", "collection", 3, Item("f1"), Item("f2"), Item("f3")), Section("recent", "recently_added", 1, Item("r1")));

    private static string[] SlideKeys(HomeViewModel home) => [.. home.Banner.Slides.Select(slide => slide.Title.Key)];

    private static string[] RowIds(HomeViewModel home) => [.. home.Rows.Select(row => row.SectionId)];

    [Fact]
    public async Task TheFeaturedRow_IsTheBanner_ItsCardsTheSlides_AndNoLongerARow()
    {
        var home = await HomeWithAsync(Section("cw", "continue_watching", 1, Item("m1", progressUpdatedAt: "2026-10-25T10:00:00Z")),
            Featured("picks", "collection", 3, Item("f1"), Item("f2"), Item("f3")),
            Section("recent", "recently_added", 1, Item("r1")));

        Assert.Equal(["f1", "f2", "f3"], SlideKeys(home));
        Assert.Equal(["cw", "recent"], RowIds(home));
        Assert.Equal("f1", home.HeroTitle?.Key);
        Assert.Equal(("01", "03"), (home.Banner.NumberText, home.Banner.CountText));
    }

    [Fact]
    public async Task OnlyTheFirstFeaturedRow_IsTheBanner_ALaterOneStaysARow()
    {
        var home = await HomeWithAsync(Featured("picks", "collection", 2, Item("f1"), Item("f2")),
            Section("recent", "recently_added", 1, Item("r1")),
            Featured("mine", "favorites", 1, Item("g1")));

        Assert.Equal(["f1", "f2"], SlideKeys(home));
        Assert.Equal(["recent", "mine"], RowIds(home));
    }

    [Fact]
    public async Task WithoutAFeaturedRow_TheHeroFollowsTheCardsAsBefore()
    {
        var home = await HomeWithAsync(Section("recent", "recently_added", 2, Item("r1"), Item("r2")));

        Assert.False(home.Banner.HasSlides);
        Assert.Equal("r1", home.HeroTitle?.Key);

        home.PointCardCommand.Execute(home.Rows[0].Cards[1]);
        Assert.Equal("r2", home.HeroTitle?.Key);
        _time.Advance(FeaturedBannerViewModel.AdvanceTime * 3);
        Assert.Equal("r2", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task AFeaturedRowWithoutCards_LeavesHomeWithoutABanner()
    {
        var home = await HomeWithAsync(Featured("picks", "collection", 0), Section("recent", "recently_added", 1, Item("r1")),
            Featured("mine", "favorites", 1, Item("g1")));

        Assert.False(home.Banner.HasSlides);
        Assert.Equal(["recent", "mine"], RowIds(home));
        Assert.Equal("r1", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task TheSlides_StopAtTheRowsItemLimit()
    {
        var home = await HomeWithAsync(Row("picks", "collection", 3, featured: true, 2, Item("f1"), Item("f2"), Item("f3")));

        Assert.Equal(["f1", "f2"], SlideKeys(home));
    }

    [Fact]
    public async Task ABannerAlone_IsNotAnEmptyHome()
    {
        var home = await HomeWithAsync(Featured("picks", "collection", 1, Item("f1")));

        Assert.Empty(home.Rows);
        Assert.False(home.IsEmpty);
        Assert.Equal("", home.StatusText);
        Assert.Equal("f1", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task TheBanner_MovesOnEveryEightSeconds_AndFromTheLastBackToTheFirst()
    {
        var home = await HomeWithThreeSlidesAsync();

        _time.Advance(FeaturedBannerViewModel.AdvanceTime - Tick);
        Assert.Equal("f1", home.HeroTitle?.Key);
        _time.Advance(Tick);
        Assert.Equal(("f2", "02"), (home.HeroTitle?.Key, home.Banner.NumberText));
        _time.Advance(FeaturedBannerViewModel.AdvanceTime);
        Assert.Equal("f3", home.HeroTitle?.Key);
        _time.Advance(FeaturedBannerViewModel.AdvanceTime);
        Assert.Equal("f1", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task PointerOrFocusOnTheBanner_HoldsItsSlide_AndLettingGoGivesItAFreshEightSeconds()
    {
        var home = await HomeWithThreeSlidesAsync();
        _time.Advance(TimeSpan.FromSeconds(5));

        home.Banner.Hold(true);
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("f1", home.HeroTitle?.Key);
        Assert.True(home.Banner.IsHeld);

        home.Banner.Hold(false);
        _time.Advance(FeaturedBannerViewModel.AdvanceTime - Tick);
        Assert.Equal("f1", home.HeroTitle?.Key);
        _time.Advance(Tick);
        Assert.Equal("f2", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task ThePauseButton_StopsTheSlideshow_AndStartsItAgain()
    {
        var home = await HomeWithThreeSlidesAsync();

        home.Banner.TogglePauseCommand.Execute(null);
        Assert.Equal((true, "Play slideshow"), (home.Banner.IsPausedByButton, home.Banner.PauseLabel));
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("f1", home.HeroTitle?.Key);

        home.Banner.TogglePauseCommand.Execute(null);
        Assert.Equal((false, "Pause slideshow"), (home.Banner.IsPausedByButton, home.Banner.PauseLabel));
        _time.Advance(FeaturedBannerViewModel.AdvanceTime);
        Assert.Equal("f2", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task ThePauseButton_HoldsUntilPressedAgain_NotUntilThePointerLeaves()
    {
        var home = await HomeWithThreeSlidesAsync();
        home.Banner.Hold(true);
        home.Banner.TogglePauseCommand.Execute(null);

        home.Banner.Hold(false);
        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal("f1", home.HeroTitle?.Key);
        Assert.True(home.Banner.IsPausedByButton);
        Assert.False(home.Banner.IsAdvancing);
    }

    [Fact]
    public async Task APauseAndAHold_OutlastARefreshOfTheSameBanner()
    {
        var home = await HomeWithThreeSlidesAsync();
        home.Banner.TogglePauseCommand.Execute(null);
        home.Banner.Hold(true);

        await ReloadAsync(home, Featured("picks", "collection", 3, Item("f1"), Item("f2"), Item("f3")), Section("recent", "recently_added", 1, Item("r1")));
        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal("f1", home.HeroTitle?.Key);
        Assert.Equal((true, true, false), (home.Banner.IsPausedByButton, home.Banner.IsHeld, home.Banner.IsAdvancing));
    }

    [Fact]
    public async Task ABannerThatGoesWhilePausedAndHeld_ComesBackRunning()
    {
        var home = await HomeWithThreeSlidesAsync();
        home.Banner.TogglePauseCommand.Execute(null);
        home.Banner.Hold(true);

        await ReloadAsync(home, Section("picks", "collection", 3, Item("f1"), Item("f2"), Item("f3")), Section("recent", "recently_added", 1, Item("r1")));
        Assert.False(home.Banner.HasSlides);
        Assert.Equal((false, false), (home.Banner.IsPausedByButton, home.Banner.IsHeld));

        await ReloadAsync(home, Featured("picks", "collection", 3, Item("f1"), Item("f2"), Item("f3")), Section("recent", "recently_added", 1, Item("r1")));
        _time.Advance(FeaturedBannerViewModel.AdvanceTime);
        Assert.Equal("f2", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task AnotherProfile_LetsGoOfThePauseAndTheHold()
    {
        var home = await HomeWithThreeSlidesAsync();
        home.Banner.TogglePauseCommand.Execute(null);
        home.Banner.Hold(true);
        _silo.Map(SectionsPath, Sections(Featured("theirs", "collection", 2, Item("k1"), Item("k2"))));

        _session.ProfileId = "8";
        _session.RaiseChanged();
        await home.Loading;

        Assert.Equal((false, false), (home.Banner.IsPausedByButton, home.Banner.IsHeld));
        _time.Advance(FeaturedBannerViewModel.AdvanceTime);
        Assert.Equal("k2", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task WithoutSlides_NothingHoldsTheBanner()
    {
        var home = await HomeWithAsync(Section("recent", "recently_added", 1, Item("r1")));

        home.Banner.Hold(true);
        Assert.False(home.Banner.IsHeld);

        await ReloadAsync(home, Featured("picks", "collection", 2, Item("f1"), Item("f2")), Section("recent", "recently_added", 1, Item("r1")));
        _time.Advance(FeaturedBannerViewModel.AdvanceTime);
        Assert.Equal("f2", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task LeavingHome_LetsGoOfTheHold_ButKeepsThePauseButtons()
    {
        var home = await HomeWithThreeSlidesAsync();
        home.Banner.Hold(true);
        home.Deactivate();
        await home.ActivateAsync();
        Assert.False(home.Banner.IsHeld);
        _time.Advance(FeaturedBannerViewModel.AdvanceTime);
        Assert.Equal("f2", home.HeroTitle?.Key);

        home.Banner.TogglePauseCommand.Execute(null);
        home.Deactivate();
        await home.ActivateAsync();
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("f2", home.HeroTitle?.Key);
        Assert.True(home.Banner.IsPausedByButton);
    }

    [Fact]
    public async Task SteppingByHandWhilePaused_StartsTheRailEmptyAgain()
    {
        var home = await HomeWithThreeSlidesAsync();
        home.Banner.TogglePauseCommand.Execute(null);
        int cycle = home.Banner.Cycle;

        home.Banner.NextCommand.Execute(null);

        Assert.Equal((false, cycle + 1), (home.Banner.IsAdvancing, home.Banner.Cycle));
    }

    [Fact]
    public async Task ARefreshThatMovesTheShownSlide_TellsTheHeroOnlyThatSlide()
    {
        var home = await HomeWithThreeSlidesAsync();
        home.Banner.NextCommand.Execute(null);
        var told = new List<string?>();
        home.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeViewModel.HeroTitle))
            {
                told.Add(home.HeroTitle?.Key);
            }
        };

        await ReloadAsync(home, Featured("picks", "collection", 4, Item("f0"), Item("f1"), Item("f2"), Item("f3")),
            Section("recent", "recently_added", 1, Item("r1")));

        Assert.Equal(["f2"], told);
        Assert.Equal("03", home.Banner.NumberText);
    }

    [Fact]
    public async Task WithoutABanner_TheHeroGetsNoSlideNumber_NorWhileHomeLoads()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _silo.Map(SectionsPath, (_, _) => answer.Task);
        var home = new HomeViewModel(_session, _silo.Catalog, _silo.Images, _navigation, new ImmediateDispatcher(), _time,
            _silo.Logs.For<HomeViewModel>(), _changes, _artwork);
        var numbers = new List<string?>();
        home.Banner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FeaturedBannerViewModel.NumberText))
            {
                numbers.Add(home.Banner.NumberText);
            }
        };

        var activation = home.ActivateAsync();
        Assert.True(home.IsLoading);
        Assert.Null(home.Banner.NumberText);
        answer.SetResult(BrowseFakeSilo.Json(Sections(Section("recent", "recently_added", 1, Item("r1")))));
        await activation;

        Assert.Null(home.Banner.NumberText);
        Assert.All(numbers, number => Assert.Null(number));
        Assert.Equal("r1", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task SteppingByHand_GoesAround_AndStartsTheEightSecondsAgain()
    {
        var home = await HomeWithThreeSlidesAsync();
        _time.Advance(TimeSpan.FromSeconds(5));

        home.Banner.NextCommand.Execute(null);
        Assert.Equal("f2", home.HeroTitle?.Key);
        _time.Advance(FeaturedBannerViewModel.AdvanceTime - Tick);
        Assert.Equal("f2", home.HeroTitle?.Key);
        _time.Advance(Tick);
        Assert.Equal("f3", home.HeroTitle?.Key);

        home.Banner.NextCommand.Execute(null);
        Assert.Equal("f1", home.HeroTitle?.Key);
        home.Banner.PreviousCommand.Execute(null);
        Assert.Equal("f3", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task EveryNewCountdown_IsTold_ForTheProgressRail()
    {
        var home = await HomeWithThreeSlidesAsync();
        Assert.True(home.Banner.IsAdvancing);
        int cycle = home.Banner.Cycle;

        _time.Advance(FeaturedBannerViewModel.AdvanceTime);
        Assert.Equal(cycle + 1, home.Banner.Cycle);

        home.Banner.Hold(true);
        Assert.False(home.Banner.IsAdvancing);
        Assert.Equal(cycle + 1, home.Banner.Cycle);

        home.Banner.Hold(false);
        Assert.Equal((true, cycle + 2), (home.Banner.IsAdvancing, home.Banner.Cycle));
    }

    [Fact]
    public async Task ASingleSlide_StaysPut()
    {
        var home = await HomeWithAsync(Featured("picks", "collection", 1, Item("f1")));

        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal("f1", home.HeroTitle?.Key);
        Assert.False(home.Banner.HasSeveral);
        Assert.False(home.Banner.IsAdvancing);
    }

    [Fact]
    public async Task LeavingHome_StopsTheSlideshow_UntilItShowsAgain()
    {
        var home = await HomeWithThreeSlidesAsync();

        home.Deactivate();
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("f1", home.HeroTitle?.Key);

        await home.ActivateAsync();
        _time.Advance(FeaturedBannerViewModel.AdvanceTime);
        Assert.Equal("f2", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task ABannerScrolledAway_DoesNotMoveOn()
    {
        var home = await HomeWithThreeSlidesAsync();

        home.Banner.IsInView = false;
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("f1", home.HeroTitle?.Key);

        home.Banner.IsInView = true;
        _time.Advance(FeaturedBannerViewModel.AdvanceTime);
        Assert.Equal("f2", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task WithAnimationsOff_TheBannerMovesOnlyByHand()
    {
        var home = await HomeWithThreeSlidesAsync();

        home.Banner.IsMotionReduced = true;
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("f1", home.HeroTitle?.Key);
        home.Banner.NextCommand.Execute(null);
        Assert.Equal("f2", home.HeroTitle?.Key);
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("f2", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task Play_PlaysAMovieItself_OrTheEpisodeTheServerNamesForASeries()
    {
        var home = await HomeWithAsync(Featured("picks", "collection", 3, Item("m1"), Item("s1", "series", playContentId: "s1e5"), Item("s2", "series")));

        Assert.True(home.Banner.CanPlay);
        home.Banner.PlayCommand.Execute(null);
        home.Banner.NextCommand.Execute(null);
        home.Banner.PlayCommand.Execute(null);
        home.Banner.NextCommand.Execute(null);

        Assert.False(home.Banner.CanPlay);
        var played = _navigation.Navigations.Where(n => n.Route == AppRoute.Player).Select(n => (PlayRequest)n.Parameter!).ToList();
        Assert.Equal(["m1", "s1e5"], played.Select(request => request.ContentId));
        Assert.All(played, request => Assert.False(request.FromBeginning));
    }

    [Fact]
    public async Task MoreInfo_OpensTheSlidesTitle()
    {
        var home = await HomeWithThreeSlidesAsync();
        home.Banner.NextCommand.Execute(null);

        home.Banner.MoreInfoCommand.Execute(null);

        Assert.Equal((AppRoute.Item, (object?)"f2"), _navigation.Navigations[^1]);
    }

    [Fact]
    public async Task PointingAtACard_LeavesTheBannersSlide()
    {
        var home = await HomeWithThreeSlidesAsync();

        home.PointCardCommand.Execute(home.Rows[0].Cards[0]);
        _time.Advance(BrowseHeroViewModel.SettleTime);

        Assert.Equal("f1", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task ARefreshThatMarksARowFeatured_TurnsTheHeroIntoTheBanner()
    {
        var home = await HomeWithAsync(Section("recent", "recently_added", 1, Item("r1")), Section("picks", "collection", 2, Item("f1"), Item("f2")));
        Assert.False(home.Banner.HasSlides);
        Assert.Equal("r1", home.HeroTitle?.Key);

        await ReloadAsync(home, Section("recent", "recently_added", 1, Item("r1")), Featured("picks", "collection", 2, Item("f1"), Item("f2")));

        Assert.Equal(["f1", "f2"], SlideKeys(home));
        Assert.Equal(["recent"], RowIds(home));
        Assert.Equal("f1", home.HeroTitle?.Key);
        _time.Advance(FeaturedBannerViewModel.AdvanceTime);
        Assert.Equal("f2", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task ARefreshThatDropsTheFlag_BringsBackTheHeroThatFollowsTheCards()
    {
        var home = await HomeWithAsync(Featured("picks", "collection", 2, Item("f1"), Item("f2")), Section("recent", "recently_added", 1, Item("r1")));

        await ReloadAsync(home, Section("picks", "collection", 2, Item("f1"), Item("f2")), Section("recent", "recently_added", 1, Item("r1")));

        Assert.False(home.Banner.HasSlides);
        Assert.False(home.Banner.IsAdvancing);
        Assert.Equal(["picks", "recent"], RowIds(home));
        Assert.Equal("f1", home.HeroTitle?.Key);
        home.PointCardCommand.Execute(home.Rows[1].Cards[0]);
        Assert.Equal("r1", home.HeroTitle?.Key);
        _time.Advance(FeaturedBannerViewModel.AdvanceTime * 2);
        Assert.Equal("r1", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task AQuietReload_KeepsTheSlideShown_AndItsCountdown()
    {
        var home = await HomeWithThreeSlidesAsync();
        _time.Advance(FeaturedBannerViewModel.AdvanceTime);
        _time.Advance(TimeSpan.FromSeconds(5));

        await ReloadAsync(home, Featured("picks", "collection", 4, Item("f0"), Item("f1"), Item("f2"), Item("f3")),
            Section("recent", "recently_added", 1, Item("r1")));
        Assert.Equal(("f2", "03", "04"), (home.HeroTitle?.Key, home.Banner.NumberText, home.Banner.CountText));

        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("f3", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task AReloadWithoutTheSlideShown_StartsAtTheFirst()
    {
        var home = await HomeWithThreeSlidesAsync();
        home.Banner.NextCommand.Execute(null);

        await ReloadAsync(home, Featured("picks", "collection", 2, Item("f1"), Item("f3")), Section("recent", "recently_added", 1, Item("r1")));

        Assert.Equal("f1", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task AnotherProfile_StartsItsOwnBannerAtTheFirstSlide()
    {
        var home = await HomeWithThreeSlidesAsync();
        home.Banner.NextCommand.Execute(null);
        _silo.Map(SectionsPath, Sections(Featured("theirs", "collection", 2, Item("k1"), Item("f2"))));

        _session.ProfileId = "8";
        _session.RaiseChanged();
        await home.Loading;

        Assert.Equal(["k1", "f2"], SlideKeys(home));
        Assert.Equal("k1", home.HeroTitle?.Key);
    }

    [Fact]
    public async Task WhileItIsInView_TheNextSlidesPicturesAreFetchedAhead()
    {
        object Art(string id) => new
        {
            content_id = id,
            type = "movie",
            title = "Title " + id,
            status = "matched",
            genres = Array.Empty<string>(),
            keywords = Array.Empty<string>(),
            backdrop_url = $"/api/v2/artwork/{id}/backdrop.webp?sig=b",
            logo_url = $"/api/v2/artwork/{id}/logo.png?sig=l",
        };
        var home = await HomeWithAsync(Featured("picks", "collection", 3, Art("f1"), Art("f2"), Art("f3")));
        Assert.Contains(_artwork.Urls, url => url.AbsolutePath.EndsWith("/f2/backdrop.webp", StringComparison.Ordinal));
        Assert.Contains(_artwork.Urls, url => url.AbsolutePath.EndsWith("/f2/logo.png", StringComparison.Ordinal));
        Assert.DoesNotContain(_artwork.Urls, url => url.AbsolutePath.Contains("/f3/", StringComparison.Ordinal));

        home.Banner.IsInView = false;
        home.Banner.NextCommand.Execute(null);
        Assert.DoesNotContain(_artwork.Urls, url => url.AbsolutePath.Contains("/f3/", StringComparison.Ordinal));

        home.Banner.IsInView = true;
        Assert.Contains(_artwork.Urls, url => url.AbsolutePath.EndsWith("/f3/backdrop.webp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemovingATitleFromAFeaturedContinueWatching_TakesItsSlideToo()
    {
        _silo.Map("api/v2/home/dismissals/continue_watching/m1", _ => BrowseFakeSilo.NoContent());
        var home = await HomeWithAsync(Featured("cw", "continue_watching", 2, Item("m1", progressUpdatedAt: "2026-10-25T10:00:00Z"),
            Item("m2", progressUpdatedAt: "2026-10-25T11:00:00Z")),
            Section("next", "next_up", 1, Item("m1", progressUpdatedAt: "2026-10-25T10:00:00Z")));

        await home.RemoveCommand.ExecuteAsync(home.Rows[0].Cards[0]);

        Assert.Equal(["m2"], SlideKeys(home));
        Assert.Equal("m2", home.HeroTitle?.Key);
    }
}
