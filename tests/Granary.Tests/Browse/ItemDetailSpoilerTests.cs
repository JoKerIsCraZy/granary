using Granary.Core.ViewModels;
using Granary.Tests.Settings;
using static Granary.Tests.Browse.ItemDetailFixture;
using static Granary.Tests.Browse.ItemDetailPayloads;

namespace Granary.Tests.Browse;

/// <summary>
/// Spoiler protection on a title's page, as the web's SeasonEpisodeGrid, EpisodeCarousel and EpisodeContent apply it: an
/// unwatched episode's still blurred, its description left out, the episode's own description behind "Show it".
/// </summary>
public sealed class ItemDetailSpoilerTests
{
    private const string Episode3Id = "episode-tmdb-96677-3-3";

    /// <summary>Season 3: the first episode watched, the second not, the third not and showing the series' art for a still.</summary>
    private const string SpoilerSeason = """
        {
          "items": [
            { "content_id": "episode-tmdb-96677-3-1", "season_number": 3, "episode_number": 1, "title": "Chapter 1",
              "overview": "Assane returns to Paris.", "still_url": "/api/v2/artwork/episodes/1/w400.webp?exp=1&sig=e1",
              "still_is_episode_still": true,
              "user_data": { "played": true, "watched_count": 1, "unplayed_count": 0, "in_progress_count": 0 } },
            { "content_id": "episode-tmdb-96677-3-2", "season_number": 3, "episode_number": 2, "title": "Chapter 2",
              "overview": "A heist goes wrong.", "still_url": "/api/v2/artwork/episodes/2/w400.webp?exp=1&sig=e2",
              "still_is_episode_still": true,
              "user_data": { "played": false, "watched_count": 0, "unplayed_count": 1, "in_progress_count": 0 } },
            { "content_id": "episode-tmdb-96677-3-3", "season_number": 3, "episode_number": 3, "title": "Chapter 3",
              "overview": "The truth comes out.", "still_url": "/api/v2/artwork/series/96677/backdrop/w400.webp?exp=1&sig=b",
              "still_is_episode_still": false }
          ]
        }
        """;

    /// <summary><see cref="SpoilerSeason"/> once the second episode was started on another device.</summary>
    private static readonly string SecondStarted = SpoilerSeason.Replace(
        """{ "played": false, "watched_count": 0, "unplayed_count": 1, "in_progress_count": 0 }""",
        """{ "played": false, "watched_count": 0, "unplayed_count": 1, "in_progress_count": 1, "is_in_progress": true, "position_seconds": 300 }""",
        StringComparison.Ordinal);

    /// <summary><see cref="SpoilerSeason"/> with its first episode not watched either.</summary>
    private static readonly string FirstUnwatched = SpoilerSeason.Replace(
        """{ "played": true, "watched_count": 1, "unplayed_count": 0, "in_progress_count": 0 }""",
        """{ "played": false, "watched_count": 0, "unplayed_count": 1, "in_progress_count": 0 }""",
        StringComparison.Ordinal);

    /// <summary><see cref="Episode"/> not watched yet.</summary>
    private static readonly string UnwatchedEpisode = Episode.Replace("\"played\": true", "\"played\": false", StringComparison.Ordinal);

    private readonly ItemDetailFixture _fixture = new();

    private async Task<ItemDetailViewModel> SeasonAsync(bool images = true, bool overviews = true, params string[] lists)
    {
        _fixture.Spoilers = await SpoilerSwitches.StartedAsync(images, overviews);
        _fixture.Get(ItemPath(Season3Id), Ok(Season))
            .Get(SeasonItemEpisodesPath(Season3Id), [.. (lists.Length == 0 ? [SpoilerSeason] : lists).Select(Ok)]);
        return await _fixture.LoadAsync(Season3Id);
    }

    private async Task<ItemDetailViewModel> UnwatchedEpisodeAsync(bool images = true, bool overviews = true)
    {
        _fixture.Spoilers = await SpoilerSwitches.StartedAsync(images, overviews);
        _fixture.Get(ItemPath(EpisodeId), Ok(UnwatchedEpisode))
            .Get(SeasonsPath(SeriesId), Ok(SeriesSeasons))
            .Get(SeriesEpisodesPath(SeriesId, 3), Ok(FirstUnwatched));
        return await _fixture.LoadAsync(EpisodeId);
    }

    [Fact]
    public async Task TheSeasonsGrid_BlursUnwatchedStills_AndLeavesTheirDescriptionsOut()
    {
        var detail = await SeasonAsync();

        Assert.Equal([false, true, false], detail.Episodes.Select(e => e.Card.BlursImage));
        Assert.Equal(["Assane returns to Paris.", null, null], detail.Episodes.Select(e => e.Overview));
        // Titles stay.
        Assert.Equal(["S03E01 · Chapter 1", "S03E02 · Chapter 2", "S03E03 · Chapter 3"], detail.Episodes.Select(e => e.Card.Title));
    }

    [Fact]
    public async Task TheSeasonsGrid_ShowsEverything_WhileTheProfileHidesNothing()
    {
        var detail = await SeasonAsync(images: false, overviews: false);

        Assert.All(detail.Episodes, e => Assert.False(e.Card.BlursImage));
        Assert.Equal(["Assane returns to Paris.", "A heist goes wrong.", "The truth comes out."], detail.Episodes.Select(e => e.Overview));
    }

    [Fact]
    public async Task EachSwitch_HidesOnlyItsOwn()
    {
        var images = await SeasonAsync(images: true, overviews: false);

        Assert.True(images.Episodes[1].Card.BlursImage);
        Assert.Equal("A heist goes wrong.", images.Episodes[1].Overview);

        var overviews = await SeasonAsync(images: false, overviews: true);

        Assert.False(overviews.Episodes[1].Card.BlursImage);
        Assert.Null(overviews.Episodes[1].Overview);
    }

    [Fact]
    public async Task WithoutTheSwitches_NothingIsHidden()
    {
        _fixture.Get(ItemPath(Season3Id), Ok(Season)).Get(SeasonItemEpisodesPath(Season3Id), Ok(SpoilerSeason));

        var detail = await _fixture.LoadAsync(Season3Id);

        Assert.All(detail.Episodes, e => Assert.False(e.Card.BlursImage));
        Assert.Equal("A heist goes wrong.", detail.Episodes[1].Overview);
    }

    [Fact]
    public async Task AnUnwatchedEpisodesPage_HidesItsDescription_UntilShowIt()
    {
        var detail = await UnwatchedEpisodeAsync();

        Assert.True(detail.IsOverviewHidden);
        Assert.False(detail.ShowsOverview);
        Assert.Equal("Description hidden until you watch. Show it", ItemDetailViewModel.OverviewHiddenText);
        // The Info dialog does not give it away either.
        Assert.DoesNotContain(detail.InfoSections.SelectMany(s => s.Rows), row => row.Label == "Overview");
        // The page's own title and its backdrop stay, as on the web.
        Assert.Equal("Chapter 1", detail.Title);
        Assert.NotNull(detail.BackdropUrl);

        detail.RevealOverviewCommand.Execute(null);

        Assert.False(detail.IsOverviewHidden);
        Assert.True(detail.ShowsOverview);
        Assert.Equal("Assane returns to Paris.", detail.Overview);
        Assert.Contains(detail.InfoSections.SelectMany(s => s.Rows), row => row.Label == "Overview");
    }

    [Fact]
    public async Task ARevealedDescription_IsHiddenAgain_OnTheNextEpisodesPage()
    {
        var detail = await UnwatchedEpisodeAsync();
        detail.RevealOverviewCommand.Execute(null);
        _fixture.Get(ItemPath(Episode2Id), Ok(UnwatchedEpisode.Replace(EpisodeId, Episode2Id, StringComparison.Ordinal)));

        await detail.LoadCommand.ExecuteAsync(Episode2Id);

        Assert.True(detail.IsOverviewHidden);
    }

    [Fact]
    public async Task AWatchedEpisodesPage_ShowsItsDescription()
    {
        _fixture.Spoilers = await SpoilerSwitches.StartedAsync();

        var detail = await _fixture.Episode().LoadAsync(EpisodeId);

        Assert.False(detail.IsOverviewHidden);
        Assert.True(detail.ShowsOverview);
    }

    [Fact]
    public async Task AnUnwatchedEpisodesPage_KeepsItsDescription_WhileOnlyImagesAreHidden()
    {
        var detail = await UnwatchedEpisodeAsync(images: true, overviews: false);

        Assert.False(detail.IsOverviewHidden);
        Assert.True(detail.ShowsOverview);
    }

    [Fact]
    public async Task MoreEpisodes_BlursUnwatchedStills_TheEpisodeItselfToo_AndLeavesTheirDescriptionsOut()
    {
        var detail = await UnwatchedEpisodeAsync();

        Assert.Equal([true, true, false], detail.MoreEpisodes.Select(c => c.BlursImage));
        Assert.Equal([true, false, false], detail.MoreEpisodes.Select(c => c.IsCurrent));
        Assert.All(detail.MoreEpisodes, c => Assert.Null(c.Caption));
    }

    [Fact]
    public async Task MarkingTheEpisodeWatched_ShowsItsDescription_AndLiftsItsBlur()
    {
        var detail = await UnwatchedEpisodeAsync();
        _fixture.Silo.On(HttpMethod.Post, "api/v2/watched/" + EpisodeId, () => Task.FromResult(NoContentResponse()));

        await detail.ToggleWatchedCommand.ExecuteAsync(null);

        Assert.True(detail.IsWatched);
        Assert.False(detail.IsOverviewHidden);
        Assert.False(Assert.Single(detail.MoreEpisodes, c => c.IsCurrent).BlursImage);
    }

    [Fact]
    public async Task ChangingTheSwitches_UpdatesTheOpenPage()
    {
        var detail = await UnwatchedEpisodeAsync();
        detail.Activate();

        await _fixture.Spoilers!.SetHidesImagesAsync(false);

        Assert.All(detail.MoreEpisodes, c => Assert.False(c.BlursImage));
        Assert.True(detail.IsOverviewHidden);

        await _fixture.Spoilers.SetHidesOverviewsAsync(false);

        Assert.False(detail.IsOverviewHidden);
        Assert.Equal("A heist goes wrong.", detail.MoreEpisodes[1].Caption);
    }

    [Fact]
    public async Task ASwitchChangedWhileThePageIsAway_ShowsWhenItIsShownAgain()
    {
        var detail = await SeasonAsync();
        detail.Activate();
        detail.Deactivate();

        await _fixture.Spoilers!.SetHidesImagesAsync(false);
        Assert.True(detail.Episodes[1].Card.BlursImage);

        detail.Activate();

        Assert.False(detail.Episodes[1].Card.BlursImage);
    }

    [Fact]
    public async Task AWatchStateChangeElsewhere_LiftsTheBlur()
    {
        var detail = await SeasonAsync(lists: [SpoilerSeason, SecondStarted]);
        detail.Activate();
        Assert.True(detail.Episodes[1].Card.BlursImage);

        _fixture.Changes.Report();
        _fixture.Clock.Advance(ItemDetailViewModel.RefreshDelay);
        await detail.Refreshing;

        Assert.False(detail.Episodes[1].Card.BlursImage);
        Assert.Equal("A heist goes wrong.", detail.Episodes[1].Overview);
        // The third is still unwatched: its description stays out.
        Assert.False(detail.Episodes[2].Card.BlursImage);
        Assert.Null(detail.Episodes[2].Overview);
    }

    [Fact]
    public async Task AWatchStateChangeElsewhere_OnAnEpisodesPage_ShowsItsDescription_AndLiftsItsBlur()
    {
        var detail = await UnwatchedEpisodeAsync();
        // What the server has once the episode was watched on another device.
        _fixture.Get(ItemPath(EpisodeId), Ok(Episode)).Get(SeriesEpisodesPath(SeriesId, 3), Ok(SpoilerSeason));
        detail.Activate();

        _fixture.Changes.Report();
        _fixture.Clock.Advance(ItemDetailViewModel.RefreshDelay);
        await detail.Refreshing;

        Assert.True(detail.IsWatched);
        Assert.False(detail.IsOverviewHidden);
        Assert.Equal([false, true, false], detail.MoreEpisodes.Select(c => c.BlursImage));
    }

    [Fact]
    public async Task AWatchStateChange_WhileThePageIsAway_AsksNothing()
    {
        var detail = await SeasonAsync(lists: [SpoilerSeason, SecondStarted]);
        detail.Activate();
        detail.Deactivate();
        int asked = _fixture.Silo.SentTo(HttpMethod.Get, SeasonItemEpisodesPath(Season3Id)).Count;

        _fixture.Changes.Report();
        _fixture.Clock.Advance(ItemDetailViewModel.RefreshDelay);
        await detail.Refreshing;

        Assert.Equal(asked, _fixture.Silo.SentTo(HttpMethod.Get, SeasonItemEpisodesPath(Season3Id)).Count);
        Assert.True(detail.Episodes[1].Card.BlursImage);
    }

    [Fact]
    public async Task AChangeElsewhereWhileThePageMarksWatched_IsReadOnceThatIsDone()
    {
        var detail = await SeasonAsync(lists: [SpoilerSeason, SecondStarted]);
        detail.Activate();
        var (held, gate) = Held();
        _fixture.Silo.On(HttpMethod.Post, "api/v2/watched/" + Season3Id, held);
        var marking = detail.ToggleWatchedCommand.ExecuteAsync(null);

        _fixture.Changes.Report();
        _fixture.Clock.Advance(ItemDetailViewModel.RefreshDelay);
        await detail.Refreshing;
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, SeasonItemEpisodesPath(Season3Id)));

        gate.SetResult(NoContentResponse());
        await marking;
        await detail.Refreshing;

        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Get, SeasonItemEpisodesPath(Season3Id)).Count);
        // The server's answer, not only the page's own marks: the third episode is still unwatched there.
        Assert.Null(detail.Episodes[2].Overview);
    }

    [Fact]
    public async Task WhileTheProfileHidesNothing_AChangeElsewhereAsksNothing()
    {
        var detail = await SeasonAsync(images: false, overviews: false, lists: [SpoilerSeason, SecondStarted]);
        detail.Activate();

        _fixture.Changes.Report();
        _fixture.Clock.Advance(ItemDetailViewModel.RefreshDelay);
        await detail.Refreshing;

        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, SeasonItemEpisodesPath(Season3Id)));
    }

    [Fact]
    public async Task ABurstOfChangesElsewhere_IsReadOnce()
    {
        var detail = await SeasonAsync(lists: [SpoilerSeason, SecondStarted]);
        detail.Activate();

        for (int i = 0; i < 5; i++)
        {
            _fixture.Changes.Report();
            _fixture.Clock.Advance(ItemDetailViewModel.RefreshDelay / 4);
        }
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, SeasonItemEpisodesPath(Season3Id)));
        _fixture.Clock.Advance(ItemDetailViewModel.RefreshDelay);
        await detail.Refreshing;

        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Get, SeasonItemEpisodesPath(Season3Id)).Count);
        Assert.False(detail.Episodes[1].Card.BlursImage);
    }

    [Fact]
    public async Task TurningTheSwitchesOn_ReadsWhatChangedWhileTheyWereOff()
    {
        var detail = await SeasonAsync(images: false, overviews: false, lists: [SpoilerSeason, SecondStarted]);
        detail.Activate();
        _fixture.Changes.Report();
        _fixture.Clock.Advance(ItemDetailViewModel.RefreshDelay);

        await _fixture.Spoilers!.SetHidesImagesAsync(true);
        _fixture.Clock.Advance(ItemDetailViewModel.RefreshDelay);
        await detail.Refreshing;

        // The second episode was started elsewhere meanwhile: its still shows.
        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Get, SeasonItemEpisodesPath(Season3Id)).Count);
        Assert.False(detail.Episodes[1].Card.BlursImage);
    }

    [Fact]
    public async Task ThePagesOwnWatchedToggle_DoesNotReadTheListsAgain()
    {
        var detail = await SeasonAsync(lists: [SpoilerSeason, SecondStarted]);
        detail.Activate();
        _fixture.Silo.On(HttpMethod.Post, "api/v2/watched/" + Season3Id, () => Task.FromResult(NoContentResponse()));

        await detail.ToggleWatchedCommand.ExecuteAsync(null);
        await detail.Refreshing;

        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, SeasonItemEpisodesPath(Season3Id)));
        // Marked watched here: every still shows.
        Assert.All(detail.Episodes, e => Assert.False(e.Card.BlursImage));
    }
}
