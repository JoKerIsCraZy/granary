using Granary.Core.ViewModels;
using static Granary.Tests.Browse.ItemDetailFixture;
using static Granary.Tests.Browse.ItemDetailPayloads;

namespace Granary.Tests.Browse;

/// <summary>
/// A title's page under the player, as Silo's apps read it again once the player is gone: when a playback stopped (the
/// player left, full or minimized) the Play pill says where the viewer left off and the episode rows show it, whatever the
/// spoiler switches say (none here).
/// </summary>
public sealed class ItemDetailWatchStateTests
{
    private readonly ItemDetailFixture _fixture = new();

    /// <summary><see cref="Episode"/> started and left at <paramref name="seconds"/>.</summary>
    private static string EpisodeAt(int seconds) => Episode.Replace(
        "\"user_data\": { \"played\": true, \"watched_count\": 1, \"unplayed_count\": 0, \"in_progress_count\": 0 }",
        "\"user_data\": { \"played\": false, \"watched_count\": 0, \"unplayed_count\": 1, \"in_progress_count\": 1, \"is_in_progress\": true, "
            + $"\"position_seconds\": {seconds}, \"duration_seconds\": 3125 }}",
        StringComparison.Ordinal);

    /// <summary><see cref="Series"/> once its episode in progress was watched to the end: the next one is to play.</summary>
    private static readonly string SeriesNext = Series
        .Replace("\"play_content_id\": \"episode-tmdb-96677-3-1\"", "\"play_content_id\": \"episode-tmdb-96677-3-2\"", StringComparison.Ordinal)
        .Replace("\"watched_count\": 12", "\"watched_count\": 13", StringComparison.Ordinal)
        .Replace("\"in_progress_count\": 1", "\"in_progress_count\": 0", StringComparison.Ordinal)
        .Replace("\"is_in_progress\": true", "\"is_in_progress\": false", StringComparison.Ordinal);

    /// <summary><see cref="SeriesSeasons"/> with season 3's episode in progress watched to the end.</summary>
    private static readonly string SeasonsNext = SeriesSeasons.Replace(
        "\"watched_count\": 4, \"unplayed_count\": 4, \"in_progress_count\": 1",
        "\"watched_count\": 5, \"unplayed_count\": 3, \"in_progress_count\": 0",
        StringComparison.Ordinal);

    /// <summary><see cref="ItemDetailPayloads.Season"/> once its second episode was started.</summary>
    private static readonly string SeasonStarted = Season
        .Replace("\"watched_count\": 0, \"unplayed_count\": 8, \"in_progress_count\": 0", "\"watched_count\": 1, \"unplayed_count\": 7, \"in_progress_count\": 1", StringComparison.Ordinal)
        .Replace("\"play_content_id\": \"episode-tmdb-96677-3-1\"", "\"play_content_id\": \"episode-tmdb-96677-3-2\"", StringComparison.Ordinal);

    /// <summary><see cref="SeasonEpisodes"/> with the second episode watched on to three quarters.</summary>
    private static readonly string SeasonEpisodesOn = SeasonEpisodes.Replace("\"position_seconds\": 1440", "\"position_seconds\": 2160", StringComparison.Ordinal);

    /// <summary><see cref="SeasonOneEpisodes"/> with the pilot watched.</summary>
    private static readonly string PilotWatched = SeasonOneEpisodes.Replace(
        "\"title\": \"Pilot\",",
        "\"title\": \"Pilot\", \"user_data\": { \"played\": true, \"watched_count\": 1, \"unplayed_count\": 0, \"in_progress_count\": 0 },",
        StringComparison.Ordinal);

    /// <summary><see cref="Movie"/> left at 40:00.</summary>
    private static readonly string MovieOn = Movie.Replace("\"position_seconds\": 1312.5", "\"position_seconds\": 2400", StringComparison.Ordinal);

    // The player's stop reached the server; the page reads once its wait is over.
    private async Task StopPlaybackAsync(ItemDetailViewModel detail)
    {
        _fixture.Stops.Track(Task.CompletedTask);
        _fixture.Clock.Advance(ItemDetailViewModel.RefreshDelay);
        await detail.Refreshing;
    }

    [Fact]
    public async Task AStoppedPlayback_ShowsWhereTheViewerLeftOff_OnAnEpisodesPage()
    {
        _fixture.Episode().Get(ItemPath(EpisodeId), Ok(EpisodeAt(600)));
        var detail = await _fixture.LoadAsync(EpisodeId);
        detail.Activate();
        Assert.Equal("Resume from 10:00", detail.PlayButtonText);
        _fixture.Get(ItemPath(EpisodeId), Ok(EpisodeAt(1500)));

        await StopPlaybackAsync(detail);

        Assert.Equal("Resume from 25:00", detail.PlayButtonText);
        Assert.Equal(EpisodeId, detail.PlayTargetId);
        Assert.True(detail.CanStartOver);
    }

    [Fact]
    public async Task AStoppedPlayback_ShowsWhereTheViewerLeftOff_OnAMoviesPage()
    {
        _fixture.Movie();
        var detail = await _fixture.LoadAsync(MovieId);
        detail.Activate();
        Assert.Equal("Resume from 21:52", detail.PlayButtonText);
        _fixture.Get(ItemPath(MovieId), Ok(MovieOn));

        await StopPlaybackAsync(detail);

        Assert.Equal("Resume from 40:00", detail.PlayButtonText);
    }

    [Fact]
    public async Task AStoppedPlayback_ShowsInAnEpisodesMoreEpisodes()
    {
        _fixture.Episode();
        var detail = await _fixture.LoadAsync(EpisodeId);
        detail.Activate();
        Assert.Equal(0.5, detail.MoreEpisodes[1].Progress);
        _fixture.Get(SeriesEpisodesPath(SeriesId, 3), Ok(SeasonEpisodesOn));

        await StopPlaybackAsync(detail);

        Assert.Equal(0.75, detail.MoreEpisodes[1].Progress);
    }

    [Fact]
    public async Task AStoppedPlayback_ShowsInASingleSeasonSeriesEpisodes_AndItsPlay()
    {
        _fixture.Series(SingleSeason);
        var detail = await _fixture.LoadAsync(SeriesId);
        detail.Activate();
        Assert.False(detail.Episodes[0].Card.IsWatched);
        _fixture.Get(ItemPath(SeriesId), Ok(SeriesNext)).Get(SeasonItemEpisodesPath("season-96677-1"), Ok(PilotWatched));

        await StopPlaybackAsync(detail);

        Assert.True(detail.Episodes[0].Card.IsWatched);
        Assert.Equal("Play Next", detail.PlayLabel);
        // It lists its episodes, not season cards: the seasons are not asked again.
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, SeasonsPath(SeriesId)));
    }

    [Fact]
    public async Task AStoppedPlayback_MovesASeriesPlayOnToTheNextEpisode_AndItsSeasonCardsFollow()
    {
        _fixture.Series();
        var detail = await _fixture.LoadAsync(SeriesId);
        detail.Activate();
        Assert.Equal("Resume", detail.PlayLabel);
        Assert.Equal("4 of 8 episodes", detail.Seasons.Single(s => s.Id == Season3Id).Caption);
        _fixture.Get(ItemPath(SeriesId), Ok(SeriesNext)).Get(SeasonsPath(SeriesId), Ok(SeasonsNext));

        await StopPlaybackAsync(detail);

        Assert.Equal("Play Next", detail.PlayButtonText);
        Assert.Equal(Episode2Id, detail.PlayTargetId);
        Assert.Equal("5 of 8 episodes", detail.Seasons.Single(s => s.Id == Season3Id).Caption);
    }

    [Fact]
    public async Task AStoppedPlayback_ShowsInASeasonsEpisodeRows_AndItsPlay()
    {
        _fixture.Season();
        var detail = await _fixture.LoadAsync(Season3Id);
        detail.Activate();
        Assert.Equal(0.5, detail.Episodes[1].Card.Progress);
        Assert.Equal("Play First Episode", detail.PlayLabel);
        _fixture.Get(ItemPath(Season3Id), Ok(SeasonStarted)).Get(SeasonItemEpisodesPath(Season3Id), Ok(SeasonEpisodesOn));

        await StopPlaybackAsync(detail);

        Assert.Equal(0.75, detail.Episodes[1].Card.Progress);
        Assert.Equal("Resume", detail.PlayLabel);
        Assert.Equal(Episode2Id, detail.PlayTargetId);
    }

    [Fact]
    public async Task AStoppedPlayback_WhileThePageIsAway_AsksNothing()
    {
        _fixture.Movie();
        var detail = await _fixture.LoadAsync(MovieId);
        detail.Activate();
        detail.Deactivate();

        await StopPlaybackAsync(detail);

        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, ItemPath(MovieId)));
    }

    [Fact]
    public async Task ACatalogChange_AsksNothing()
    {
        _fixture.Movie();
        var detail = await _fixture.LoadAsync(MovieId);
        detail.Activate();

        // A scan's burst: titles came and changed, the viewer's marks did not.
        for (int i = 0; i < 3; i++)
        {
            _fixture.Changes.ReportCatalog();
            _fixture.Clock.Advance(ItemDetailViewModel.RefreshDelay);
        }
        await detail.Refreshing;

        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, ItemPath(MovieId)));
    }

    [Fact]
    public async Task AChangeWhileThePlayerIsUp_AsksNothing_UntilItsStopEnded_ThenReadsOnce()
    {
        _fixture.Movie();
        var detail = await _fixture.LoadAsync(MovieId);
        detail.Activate();
        var playing = _fixture.Stops.Playing();
        _fixture.Get(ItemPath(MovieId), Ok(MovieOn));

        // Another device, twice, while the player is up (full or minimized).
        for (int i = 0; i < 2; i++)
        {
            _fixture.Changes.Report();
            _fixture.Clock.Advance(ItemDetailViewModel.RefreshDelay);
            await detail.Refreshing;
        }
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, ItemPath(MovieId)));
        Assert.Equal("Resume from 21:52", detail.PlayButtonText);

        playing.Dispose();
        await StopPlaybackAsync(detail);

        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Get, ItemPath(MovieId)).Count);
        Assert.Equal("Resume from 40:00", detail.PlayButtonText);
    }

    [Fact]
    public async Task ARefresh_LeavesAFavoriteAndAWatchlistChangeOnTheirWayAlone()
    {
        _fixture.Movie();
        var detail = await _fixture.LoadAsync(MovieId);
        detail.Activate();
        var (favoriteHeld, favoriteGate) = Held();
        var (watchlistHeld, watchlistGate) = Held();
        _fixture.Silo.On(HttpMethod.Put, "api/v2/favorites/" + MovieId, favoriteHeld);
        _fixture.Silo.On(HttpMethod.Put, "api/v2/watchlist/" + MovieId, watchlistHeld);
        var favoring = detail.ToggleFavoriteCommand.ExecuteAsync(null);
        var listing = detail.ToggleWatchlistCommand.ExecuteAsync(null);

        // The server's answer still has neither.
        await StopPlaybackAsync(detail);
        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Get, ItemPath(MovieId)).Count);
        Assert.True(detail.IsFavorite);
        Assert.True(detail.IsInWatchlist);

        favoriteGate.SetResult(NoContentResponse());
        watchlistGate.SetResult(NoContentResponse());
        await favoring;
        await listing;

        Assert.True(detail.IsFavorite);
        Assert.True(detail.IsInWatchlist);
    }

    [Fact]
    public async Task AReadThatFails_AfterAStoppedPlayback_KeepsThePageAsItWas()
    {
        _fixture.Movie();
        var detail = await _fixture.LoadAsync(MovieId);
        detail.Activate();
        _fixture.Get(ItemPath(MovieId), Busy());

        await StopPlaybackAsync(detail);

        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Get, ItemPath(MovieId)).Count);
        Assert.True(detail.IsReady);
        Assert.Equal("Resume from 21:52", detail.PlayButtonText);
        Assert.False(detail.HasActionError);
    }
}
