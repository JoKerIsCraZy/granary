using Granary.Core.Playback;
using Granary.Core.Settings;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using Granary.Tests.Settings;
using static Granary.Tests.Browse.BrowsePayloads;

namespace Granary.Tests.Browse;

/// <summary>Next Up and Continue Watching blur an unwatched episode's still, as the web's SectionRow and ContinueWatchingCard.</summary>
public sealed class HomeSpoilerTests
{
    private const string SectionsPath = "api/v2/home/sections";

    private readonly BrowseFakeSilo _silo = new();
    private readonly FakeSessionManager _session = BrowseFakeSilo.ReadySession();
    private readonly ViewerStateChanges _changes = new(new PlaybackStops());

    private HomeViewModel Create(EpisodeSpoilerSettings? spoilers) =>
        new(_session, _silo.Catalog, _silo.Images, new RecordingNavigation(), new ImmediateDispatcher(), new TestClock(ManualTimeProvider.DefaultStart),
            _silo.Logs.For<HomeViewModel>(), _changes, spoilers: spoilers);

    // An episode card as Home's rows send it: its backdrop is the episode's own still.
    private static object Episode(string id, double? position = null, bool played = false, bool? backdropIsStill = true) => new
    {
        content_id = id,
        type = "episode",
        title = "Chapter " + id,
        status = "matched",
        genres = Array.Empty<string>(),
        keywords = Array.Empty<string>(),
        series_id = "series-1",
        series_title = "Lupin",
        season_number = 1,
        episode_number = 2,
        item_source = position is null ? "next_up" : "in_progress",
        position_seconds = position,
        duration_seconds = position is null ? (double?)null : 3000,
        progress_updated_at = position is null ? null : "2026-10-25T10:00:00Z",
        backdrop_url = $"/api/v2/artwork/episodes/{id}/backdrop.webp?exp=1&sig=b",
        backdrop_is_episode_still = backdropIsStill,
        poster_url = "/api/v2/artwork/series/1/poster.webp?exp=1&sig=p",
        user_state = new { played, is_favorite = false, in_watchlist = false, not_interested = false },
    };

    private void Rows(object nextUp) => _silo.Map(SectionsPath, Sections(
        Section("next", "next_up", 2, nextUp, Episode("fallback", backdropIsStill: false)),
        Section("cw", "continue_watching", 2, Episode("started", position: 600), Item("m1", progressUpdatedAt: "2026-10-25T09:00:00Z")),
        Section("recent", "recently_added", 1, Episode("new"))));

    [Fact]
    public async Task NextUp_BlursAnUnwatchedEpisodesStill_ContinueWatchingAStartedOneNot()
    {
        Rows(Episode("next"));
        var home = Create(await SpoilerSwitches.StartedAsync());

        await home.ActivateAsync();

        Assert.Equal([true, false], home.Rows[0].Cards.Select(c => c.BlursImage));
        Assert.Equal([false, false], home.Rows[1].Cards.Select(c => c.BlursImage));
        // Other rows are posters, never blurred, as on the web.
        Assert.False(Assert.Single(home.Rows[2].Cards).BlursImage);
        // The titles stay.
        Assert.Equal("Lupin", home.Rows[0].Cards[0].Title);
    }

    [Fact]
    public async Task NothingIsBlurred_WhileTheProfileHidesNoImages()
    {
        Rows(Episode("next"));
        var home = Create(await SpoilerSwitches.StartedAsync(images: false, overviews: true));

        await home.ActivateAsync();

        Assert.All(home.Rows.SelectMany(r => r.Cards), card => Assert.False(card.BlursImage));
    }

    [Fact]
    public async Task ChangingTheSwitch_ShowsOnHomeAtOnce_InTheRowsAsTheyAre()
    {
        Rows(Episode("next"));
        var spoilers = await SpoilerSwitches.StartedAsync();
        var home = Create(spoilers);
        await home.ActivateAsync();
        var nextUp = home.Rows[0];
        var started = home.Rows[1].Cards[0];

        await spoilers.SetHidesImagesAsync(false);

        Assert.Same(nextUp, home.Rows[0]);
        Assert.False(nextUp.Cards[0].BlursImage);
        Assert.Same(started, home.Rows[1].Cards[0]);
        Assert.Single(_silo.To(SectionsPath));

        await spoilers.SetHidesImagesAsync(true);

        Assert.True(nextUp.Cards[0].BlursImage);
    }

    [Fact]
    public async Task ASwitchChangedWhileAway_ShowsOnTheNextVisit()
    {
        Rows(Episode("next"));
        var spoilers = await SpoilerSwitches.StartedAsync(images: false);
        var home = Create(spoilers);
        await home.ActivateAsync();
        home.Deactivate();

        await spoilers.SetHidesImagesAsync(true);
        Assert.False(home.Rows[0].Cards[0].BlursImage);

        await home.ActivateAsync();

        Assert.True(home.Rows[0].Cards[0].BlursImage);
        Assert.Single(_silo.To(SectionsPath));
    }

    [Fact]
    public async Task ASwitchChange_LeavesTheBannerAsItIs()
    {
        // The hero banner is the top of Home, which follows later: its slides, the one shown and its hold stay.
        _silo.Map(SectionsPath, Sections(
            Featured("next", "next_up", 3, Episode("e1"), Episode("e2"), Episode("e3")),
            Section("cw", "continue_watching", 1, Episode("unwatched"))));
        var spoilers = await SpoilerSwitches.StartedAsync(images: false);
        var home = Create(spoilers);
        await home.ActivateAsync();
        home.Banner.NextCommand.Execute(null);
        home.Banner.Hold(true);
        var slides = home.Banner.Slides;
        var shown = home.Banner.Current;
        int cycle = home.Banner.Cycle;

        await spoilers.SetHidesImagesAsync(true);

        Assert.True(home.Rows[0].Cards[0].BlursImage);
        Assert.Same(slides, home.Banner.Slides);
        Assert.Same(shown, home.Banner.Current);
        Assert.Equal(1, home.Banner.Index);
        Assert.True(home.Banner.IsHeld);
        Assert.Equal(cycle, home.Banner.Cycle);
    }

    [Fact]
    public async Task StartingTheEpisode_LiftsItsBlur()
    {
        Rows(Episode("next"));
        var home = Create(await SpoilerSwitches.StartedAsync());
        await home.ActivateAsync();
        Rows(Episode("next", position: 30));

        _changes.Report();
        await home.Loading;

        Assert.False(home.Rows[0].Cards[0].BlursImage);
    }

    [Fact]
    public async Task WithoutTheSwitches_NothingIsBlurred()
    {
        Rows(Episode("next"));
        var home = Create(spoilers: null);

        await home.ActivateAsync();

        Assert.All(home.Rows.SelectMany(r => r.Cards), card => Assert.False(card.BlursImage));
    }
}
