using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Core.Settings;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using Granary.Tests.Settings;
using static Granary.Tests.Browse.BrowsePayloads;
using static Granary.Tests.Browse.HomeSpoilerTests;

namespace Granary.Tests.Browse;

/// <summary>
/// The top of Home follows the spoiler switches: the banner's slides as the web's <c>HeroBanner</c>, and the hero that follows
/// the cards by the same rule, so pointing at a card does not show the still the card blurs.
/// </summary>
public sealed class HomeHeroSpoilerTests
{
    private const string SectionsPath = "api/v2/home/sections";
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);

    private readonly BrowseFakeSilo _silo = new();
    private readonly ManualTimeProvider _time = new();
    private readonly FakeSessionManager _session = BrowseFakeSilo.ReadySession();
    private readonly ViewerStateChanges _changes = new(new PlaybackStops());

    private async Task<HomeViewModel> HomeWithAsync(EpisodeSpoilerSettings? spoilers, params object[] sections)
    {
        _silo.Map(SectionsPath, Sections(sections));
        var home = new HomeViewModel(_session, _silo.Catalog, _silo.Images, new RecordingNavigation(), new ImmediateDispatcher(), _time,
            _silo.Logs.For<HomeViewModel>(), _changes, spoilers: spoilers);
        await home.ActivateAsync();
        return home;
    }

    private async Task ReloadAsync(HomeViewModel home, params object[] sections)
    {
        _silo.Map(SectionsPath, Sections(sections));
        _changes.Report();
        await home.Loading;
    }

    private static object Movie(string id) => new
    {
        content_id = id,
        type = "movie",
        title = "Title " + id,
        overview = "A heist in Paris.",
        status = "matched",
        genres = Array.Empty<string>(),
        keywords = Array.Empty<string>(),
        backdrop_url = $"/api/v2/artwork/{id}/backdrop.webp?sig=b",
    };

    // A series or a season, with a backdrop of unknown provenance, which for an episode would count as its still.
    private static object Show(string id, string type) => new
    {
        content_id = id,
        type,
        title = "Title " + id,
        overview = "A gentleman thief.",
        status = "matched",
        genres = Array.Empty<string>(),
        keywords = Array.Empty<string>(),
        backdrop_url = $"/api/v2/artwork/{id}/backdrop.webp?sig=b",
        user_state = new { played = false, is_favorite = false, in_watchlist = false, not_interested = false },
    };

    private static object ThreeEpisodes(object? second = null) =>
        Featured("next", "next_up", 3, Episode("e1"), second ?? Episode("e2"), Episode("e3"));

    private static (string? Key, bool? Blurs, bool HasOverview) Shown(HomeViewModel home) =>
        (home.HeroTitle?.Key, home.HeroTitle?.BlursBackdrop, home.HeroTitle?.Overview is not null);

    // The UI thread's queue: posted work runs after what is running now.
    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _queue = new();

        public bool HasThreadAccess => true;

        public void Post(Action action) => _queue.Enqueue(action);

        public void RunPending()
        {
            while (_queue.TryDequeue(out var action))
            {
                action();
            }
        }
    }

    [Fact]
    public async Task TheBanner_BlursAnUnwatchedEpisodesStill_AndHidesItsDescription_ButKeepsItsNames()
    {
        var home = await HomeWithAsync(await SpoilerSwitches.StartedAsync(), Featured("next", "next_up", 1, Episode("e1")));

        var slide = home.Banner.Current!.Title;
        Assert.True(slide.BlursBackdrop);
        Assert.Null(slide.Overview);
        Assert.Equal(("Lupin", "S1 E2 · Chapter e1"), (slide.Heading, slide.Meta));
        Assert.Same(slide, home.HeroTitle);
    }

    [Fact]
    public async Task TheBanner_KeepsAStartedOrWatchedEpisode_AMovie_AndASeriesBackdrop_Sharp()
    {
        var home = await HomeWithAsync(await SpoilerSwitches.StartedAsync(), Featured("mix", "collection", 4,
            Episode("started", position: 600), Episode("watched", played: true), Movie("m1"), Episode("fallback", backdropIsStill: false)));

        Assert.Equal(["started", "watched", "m1", "fallback"], home.Banner.Slides.Select(s => s.Title.Key));
        Assert.All(home.Banner.Slides, slide => Assert.False(slide.Title.BlursBackdrop));
        // The description goes for every unwatched episode, whatever its picture.
        Assert.Equal([true, true, true, false], home.Banner.Slides.Select(s => s.Title.Overview is not null));
    }

    [Fact]
    public async Task ASeriesAndASeason_StaySharp_WithTheirOverview_InTheBannerAndTheCardHero()
    {
        var spoilers = await SpoilerSwitches.StartedAsync();

        var banner = await HomeWithAsync(spoilers, Featured("picks", "collection", 2, Show("s1", "series"), Show("s1-1", "season")));
        Assert.All(banner.Banner.Slides, slide =>
            Assert.Equal((false, true), (slide.Title.BlursBackdrop, slide.Title.Overview is not null)));

        var cards = await HomeWithAsync(spoilers, Section("picks", "collection", 2, Show("s1", "series"), Show("s1-1", "season")));
        Assert.Equal(("s1", false, true), Shown(cards));
        cards.PointCardCommand.Execute(cards.Rows[0].Cards[1]);
        Assert.Equal(("s1-1", false, true), Shown(cards));
    }

    [Fact]
    public async Task AnEpisodeWhoseBackdropsProvenanceIsUnknown_IsBlurred_AsAnOlderServerSendsIt()
    {
        var spoilers = await SpoilerSwitches.StartedAsync();

        var banner = await HomeWithAsync(spoilers, Featured("next", "next_up", 1, Episode("old", backdropIsStill: null)));
        Assert.Equal(("old", true, false), Shown(banner));

        var cards = await HomeWithAsync(spoilers, Section("next", "next_up", 1, Episode("old", backdropIsStill: null)));
        Assert.Equal(("old", true, false), Shown(cards));
    }

    [Fact]
    public async Task UntilTheSwitchesAreRead_AnUnwatchedEpisodesStillShowsBlurred_ThenAsTheySay()
    {
        // Home can load before the profile's switches: a still shown sharp then blurred a moment later would give it away.
        var spoilers = SpoilerSwitches.Unread(images: false, overviews: false);
        var home = await HomeWithAsync(spoilers, Featured("mix", "collection", 4,
            Episode("e1"), Episode("e2"), Movie("m1"), Episode("watched", played: true)));
        Assert.Equal(EpisodeSpoilerState.Loading, spoilers.State);
        Assert.Equal([true, true, false, false], home.Banner.Slides.Select(s => s.Title.BlursBackdrop));
        // Only the picture waits.
        Assert.All(home.Banner.Slides, slide => Assert.NotNull(slide.Title.Overview));
        home.Banner.NextCommand.Execute(null);
        int cycle = home.Banner.Cycle;

        await spoilers.StartAsync();

        Assert.All(home.Banner.Slides, slide => Assert.False(slide.Title.BlursBackdrop));
        Assert.Equal(("e2", false, true), Shown(home));
        Assert.Equal((1, cycle), (home.Banner.Index, home.Banner.Cycle));
        Assert.Single(_silo.To(SectionsPath));
    }

    [Fact]
    public async Task UntilTheSwitchesAreRead_TheCardHeroBlursAnUnwatchedEpisodesStill_AndKeepsItSoWhenTheyHideIt()
    {
        var spoilers = SpoilerSwitches.Unread(images: true, overviews: false);
        var home = await HomeWithAsync(spoilers, Section("next", "next_up", 2, Episode("e1"), Movie("m1")));
        Assert.Equal(("e1", true, true), Shown(home));
        home.PointCardCommand.Execute(home.Rows[0].Cards[1]);
        Assert.Equal(("m1", false, true), Shown(home));
        home.PointCardCommand.Execute(home.Rows[0].Cards[0]);
        _time.Advance(BrowseHeroViewModel.SettleTime);
        Assert.Equal(("e1", true, true), Shown(home));

        await spoilers.StartAsync();

        Assert.Equal(("e1", true, true), Shown(home));
        Assert.True(home.Rows[0].Cards[0].BlursImage);
    }

    [Fact]
    public async Task TheSwitchesRead_NeverShowTheStillSharpOnTheWay()
    {
        // The read sets its state just before the switches it brought; as on the UI thread, posted work runs after both.
        var dispatcher = new QueuedDispatcher();
        var spoilers = SpoilerSwitches.Unread(images: true, overviews: false);
        _silo.Map(SectionsPath, Sections(Featured("next", "next_up", 1, Episode("e1"))));
        var home = new HomeViewModel(_session, _silo.Catalog, _silo.Images, new RecordingNavigation(), dispatcher, _time,
            _silo.Logs.For<HomeViewModel>(), _changes, spoilers: spoilers);
        await home.ActivateAsync();
        Assert.Equal(("e1", true, true), Shown(home));
        var told = new List<bool?>();
        home.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeViewModel.HeroTitle))
            {
                told.Add(home.HeroTitle?.BlursBackdrop);
            }
        };

        await spoilers.StartAsync();
        dispatcher.RunPending();

        Assert.All(told, blurs => Assert.True(blurs));
        Assert.Equal(("e1", true, true), Shown(home));
    }

    [Fact]
    public async Task EachSwitch_HidesOnlyItsOwn()
    {
        var images = await HomeWithAsync(await SpoilerSwitches.StartedAsync(images: true, overviews: false), ThreeEpisodes());
        Assert.Equal(("e1", true, true), Shown(images));

        var overviews = await HomeWithAsync(await SpoilerSwitches.StartedAsync(images: false, overviews: true), ThreeEpisodes());
        Assert.Equal(("e1", false, false), Shown(overviews));
    }

    [Fact]
    public async Task WithoutTheSwitches_TheTopOfHomeHidesNothing()
    {
        var home = await HomeWithAsync(spoilers: null, ThreeEpisodes());

        Assert.Equal(("e1", false, true), Shown(home));
    }

    [Fact]
    public async Task TheHeroFollowingTheCards_BlursAPointedUnwatchedEpisode_LikeItsCard()
    {
        var home = await HomeWithAsync(await SpoilerSwitches.StartedAsync(),
            Section("recent", "recently_added", 1, Movie("m1")),
            Section("next", "next_up", 2, Episode("next"), Episode("fallback", backdropIsStill: false)),
            Section("cw", "continue_watching", 1, Episode("started", position: 600)));
        Assert.Equal(("m1", false, true), Shown(home));

        home.PointCardCommand.Execute(home.Rows[1].Cards[0]);
        Assert.True(home.Rows[1].Cards[0].BlursImage);
        Assert.Equal(("next", true, false), Shown(home));

        home.PointCardCommand.Execute(home.Rows[1].Cards[1]);
        _time.Advance(BrowseHeroViewModel.SettleTime);
        Assert.Equal(("fallback", false, false), Shown(home));

        home.PointCardCommand.Execute(home.Rows[2].Cards[0]);
        _time.Advance(BrowseHeroViewModel.SettleTime);
        Assert.Equal(("started", false, true), Shown(home));
    }

    [Fact]
    public async Task ASwitchChange_UpdatesTheBannerInPlace_KeepingItsSlideAndCountdown()
    {
        var spoilers = await SpoilerSwitches.StartedAsync(images: false, overviews: false);
        var home = await HomeWithAsync(spoilers, ThreeEpisodes(), Section("cw", "continue_watching", 1, Episode("unwatched")));
        home.Banner.NextCommand.Execute(null);
        _time.Advance(TimeSpan.FromSeconds(5));
        int cycle = home.Banner.Cycle;
        Assert.Equal(("e2", false, true), Shown(home));

        await spoilers.SetHidesImagesAsync(true);
        await spoilers.SetHidesOverviewsAsync(true);

        Assert.Equal(("e2", true, false), Shown(home));
        Assert.Equal((1, cycle, true), (home.Banner.Index, home.Banner.Cycle, home.Banner.IsAdvancing));
        Assert.All(home.Banner.Slides, slide => Assert.True(slide.Title.BlursBackdrop));
        Assert.True(home.Rows[0].Cards[0].BlursImage);
        Assert.Single(_silo.To(SectionsPath));
        // The countdown runs on from where it was.
        _time.Advance(TimeSpan.FromSeconds(3) - Tick);
        Assert.Equal("e2", home.HeroTitle?.Key);
        _time.Advance(Tick);
        Assert.Equal(("e3", true, false), Shown(home));
    }

    [Fact]
    public async Task ASwitchChange_KeepsTheBannersHoldAndPause()
    {
        var spoilers = await SpoilerSwitches.StartedAsync(images: false);
        var home = await HomeWithAsync(spoilers, ThreeEpisodes());
        home.Banner.NextCommand.Execute(null);
        home.Banner.Hold(true);
        home.Banner.TogglePauseCommand.Execute(null);
        int cycle = home.Banner.Cycle;

        await spoilers.SetHidesImagesAsync(true);

        Assert.Equal(("e2", true, false), Shown(home));
        Assert.Equal((1, true, true, cycle), (home.Banner.Index, home.Banner.IsHeld, home.Banner.IsPausedByButton, home.Banner.Cycle));
    }

    [Fact]
    public async Task ASwitchChange_UpdatesTheCardHeroInPlace_AndTheCardWaitingToSettle()
    {
        var spoilers = await SpoilerSwitches.StartedAsync(images: false, overviews: false);
        var home = await HomeWithAsync(spoilers, Section("next", "next_up", 3, Episode("e1"), Episode("e2"), Episode("e3")));
        home.PointCardCommand.Execute(home.Rows[0].Cards[1]);
        home.PointCardCommand.Execute(home.Rows[0].Cards[2]);

        await spoilers.SetHidesImagesAsync(true);

        Assert.Equal(("e2", true, true), Shown(home));
        _time.Advance(BrowseHeroViewModel.SettleTime);
        Assert.Equal(("e3", true, true), Shown(home));
        Assert.Single(_silo.To(SectionsPath));
    }

    [Fact]
    public async Task StartingTheShownEpisode_LiftsTheBannersBlur_KeepingTheSlideAndItsCountdown()
    {
        var home = await HomeWithAsync(await SpoilerSwitches.StartedAsync(), ThreeEpisodes());
        home.Banner.NextCommand.Execute(null);
        _time.Advance(TimeSpan.FromSeconds(5));
        int cycle = home.Banner.Cycle;
        Assert.Equal(("e2", true, false), Shown(home));

        await ReloadAsync(home, ThreeEpisodes(second: Episode("e2", position: 30)));

        Assert.Equal(("e2", false, true), Shown(home));
        Assert.Equal((1, cycle), (home.Banner.Index, home.Banner.Cycle));
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(("e3", true, false), Shown(home));
    }

    [Fact]
    public async Task StartingThePointedEpisode_LiftsTheHerosBlur()
    {
        var home = await HomeWithAsync(await SpoilerSwitches.StartedAsync(), Section("next", "next_up", 2, Movie("m1"), Episode("e1")));
        home.PointCardCommand.Execute(home.Rows[0].Cards[1]);
        Assert.Equal(("e1", true, false), Shown(home));

        await ReloadAsync(home, Section("next", "next_up", 2, Movie("m1"), Episode("e1", played: true)));

        Assert.Equal(("e1", false, true), Shown(home));
    }
}
