using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Detail;
using static Granary.Tests.Browse.ItemDetailFixture;
using static Granary.Tests.Browse.ItemDetailPayloads;
using static Granary.Tests.Browse.SeasonListing;

namespace Granary.Tests.Browse;

/// <summary>
/// An episode's "More Episodes" holds the places its season's grid shows: missing and upcoming episodes at their numbers
/// among the listed ones, from the same calendar answers. The clock stands on Thursday, 1 October 2026, at noon in UTC.
/// </summary>
public sealed class ItemDetailMoreEpisodesPlacesTests
{
    private const string Listing = """
        { "items": [
          { "content_id": "episode-tmdb-96677-3-1", "season_number": 3, "episode_number": 1, "title": "Chapter 1", "air_date": "2026-09-01" },
          { "content_id": "episode-tmdb-96677-3-2", "season_number": 3, "episode_number": 2, "title": "Chapter 2", "air_date": "2026-09-08" },
          { "content_id": "episode-tmdb-96677-3-4", "season_number": 3, "episode_number": 4, "title": "Chapter 4", "air_date": "2026-09-22" },
          { "content_id": "episode-tmdb-96677-3-5", "season_number": 3, "episode_number": 5, "title": "Chapter 5", "air_date": "2026-09-29" }
        ] }
        """;

    private readonly ItemDetailFixture _fixture = new();
    private readonly FakeCalendar _calendar = new();

    public ItemDetailMoreEpisodesPlacesTests()
    {
        _fixture.Airings = new SeriesAirings(new CalendarApi(_fixture.Silo.Http), _fixture.Session, _fixture.Clock);
        _fixture.Spans = new EpisodeSpans(new CatalogApi(_fixture.Silo.Http), _fixture.Session, _fixture.Clock);
        _fixture.Silo.OnUrl(HttpMethod.Get, FakeCalendar.Path, _calendar.RespondAsync);
    }

    [Fact]
    public async Task MoreEpisodes_HoldsTheMissingAndUpcomingEpisodes_AtTheirNumbers()
    {
        _fixture.Episode().Get(SeriesEpisodesPath(SeriesId, 3), Ok(Listing));
        _calendar.Airs(3, "The Heist", "2026-09-15").Airs(6, "Chapter 6", "2026-10-06");
        _fixture.Guard(2);

        var detail = await LoadAsync(EpisodeId);

        Assert.Equal(["S03E01 · Chapter 1", "S03E02 · Chapter 2", "S03E03 · The Heist", "S03E04 · Chapter 4", "S03E05 · Chapter 5", "S03E06 · Chapter 6"],
            Titles(detail));
        var places = detail.MoreEpisodesRow.OfType<DetailEpisodePlaceholder>().ToList();
        Assert.Equal([true, false], places.Select(p => p.IsMissing));
        Assert.Equal(["15 Sep 2026", "Airs Tue, Oct 6"], places.Select(p => p.Detail));
        Assert.Equal(["Episode 3, missing", "Episode 6, airs October 6"], places.Select(p => p.AccessibleName));
        // The cards are the listed episodes alone, this one marked; a place is no card to open or play.
        Assert.Equal([EpisodeIdOf(1), EpisodeIdOf(2), EpisodeIdOf(4), EpisodeIdOf(5)], detail.MoreEpisodes.Select(c => c.Id));
        Assert.Equal(detail.MoreEpisodes, detail.MoreEpisodesRow.OfType<MediaCard>());
        Assert.True(detail.MoreEpisodes[0].IsCurrent);
        Assert.True(detail.HasMoreEpisodes);
    }

    [Fact]
    public async Task TheListedEpisodes_ShowAtOnce_AndThePlacesWhenTheCalendarAnswers()
    {
        _fixture.Episode().Get(SeriesEpisodesPath(SeriesId, 3), Ok(Listing));
        _calendar.Airs(3, "The Heist", "2026-09-15");
        _calendar.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Guard(2);
        var detail = _fixture.Create();

        await detail.LoadCommand.ExecuteAsync(EpisodeId);

        Assert.False(detail.Placing.IsCompleted);
        Assert.Equal(["S03E01 · Chapter 1", "S03E02 · Chapter 2", "S03E04 · Chapter 4", "S03E05 · Chapter 5"], Titles(detail));
        var cards = detail.MoreEpisodes;
        _calendar.Gate.SetResult();
        await detail.Placing;
        Assert.Equal("S03E03 · The Heist", Assert.IsType<DetailEpisodePlaceholder>(detail.MoreEpisodesRow[2]).Title);
        // The cards stay the same.
        Assert.Same(cards, detail.MoreEpisodes);
        AssertQuiet();
    }

    [Fact]
    public async Task AFailedCalendarRequest_ShowsTheRowAsListed()
    {
        _fixture.Episode().Get(SeriesEpisodesPath(SeriesId, 3), Ok(Listing));
        _calendar.Fails = true;

        var detail = await _fixture.LoadAsync(EpisodeId);

        Assert.Equal(["S03E01 · Chapter 1", "S03E02 · Chapter 2", "S03E04 · Chapter 4", "S03E05 · Chapter 5"], Titles(detail));
        Assert.Contains(_fixture.Logs.Lines, line => line.StartsWith("Warning ItemDetailViewModel:", StringComparison.Ordinal)
            && line.Contains("503", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASeasonOfOneListedEpisode_ShowsTheRow_OnceAPlaceJoinsIt()
    {
        _fixture.Episode().Get(SeriesEpisodesPath(SeriesId, 3), Ok(Episodes((1, "2026-09-24"))));
        _calendar.Airs(2, "Chapter 2", "2026-10-08");
        _calendar.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detail = _fixture.Create();

        await detail.LoadCommand.ExecuteAsync(EpisodeId);

        Assert.False(detail.HasMoreEpisodes);
        Assert.Empty(detail.MoreEpisodesRow);
        _calendar.Gate.SetResult();
        await detail.Placing;
        Assert.True(detail.HasMoreEpisodes);
        Assert.Equal(["S03E01 · Chapter 1", "S03E02 · Chapter 2"], Titles(detail));
        Assert.Equal([EpisodeIdOf(1)], detail.MoreEpisodes.Select(c => c.Id));
    }

    [Fact]
    public async Task TheSeasonsPage_AndItsEpisodesPage_ShowTheSamePlaces_FromTheSameAnswers()
    {
        _fixture.Season().Get(SeasonItemEpisodesPath(Season3Id), Ok(Listing));
        _fixture.Episode().Get(SeriesEpisodesPath(SeriesId, 3), Ok(Listing));
        _calendar.Airs(3, "The Heist", "2026-09-15").Airs(6, "Chapter 6", "2026-10-06");
        _fixture.Guard(2);

        var season = await LoadAsync(Season3Id);
        int asked = _fixture.Silo.SentTo(HttpMethod.Get, FakeCalendar.Path).Count;
        var episode = await LoadAsync(EpisodeId);

        Assert.Equal(2, season.EpisodeGrid.OfType<DetailEpisodePlaceholder>().Count());
        Assert.Equal(season.EpisodeGrid.OfType<DetailEpisodePlaceholder>(), episode.MoreEpisodesRow.OfType<DetailEpisodePlaceholder>());
        // The episode page's windows are the season page's, kept for a few minutes; so is the read of episode 2's files.
        Assert.Equal(asked, _fixture.Silo.SentTo(HttpMethod.Get, FakeCalendar.Path).Count);
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, ItemPath(EpisodeIdOf(2))));
    }

    [Fact]
    public async Task AStoppedPlayback_KeepsThePlacesInTheRow()
    {
        _fixture.Episode().Get(SeriesEpisodesPath(SeriesId, 3), Ok(Listing));
        _calendar.Airs(3, "The Heist", "2026-09-15");
        _fixture.Guard(2);
        var detail = await LoadAsync(EpisodeId);
        detail.Activate();
        _fixture.Get(SeriesEpisodesPath(SeriesId, 3), Ok(Listing.Replace("\"title\": \"Chapter 2\",",
            "\"title\": \"Chapter 2\", \"user_data\": { \"played\": true, \"watched_count\": 1, \"unplayed_count\": 0, \"in_progress_count\": 0 },",
            StringComparison.Ordinal)));

        _fixture.Stops.Track(Task.CompletedTask);
        _fixture.Clock.Advance(ItemDetailViewModel.RefreshDelay);
        await detail.Refreshing;

        Assert.True(detail.MoreEpisodes[1].IsWatched);
        Assert.Equal(["S03E01 · Chapter 1", "S03E02 · Chapter 2", "S03E03 · The Heist", "S03E04 · Chapter 4", "S03E05 · Chapter 5"], Titles(detail));
        Assert.Same(detail.MoreEpisodes[1], detail.MoreEpisodesRow[1]);
    }

    [Fact]
    public async Task AnotherTitleLoadedMeanwhile_GetsNoneOfTheEpisodesPlaces()
    {
        _fixture.Episode().Get(SeriesEpisodesPath(SeriesId, 3), Ok(Listing));
        _fixture.Movie();
        _calendar.Airs(3, "The Heist", "2026-09-15");
        _calendar.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Guard(2);
        var detail = _fixture.Create();

        await detail.LoadCommand.ExecuteAsync(EpisodeId);
        var placing = detail.Placing;
        await detail.LoadCommand.ExecuteAsync(MovieId);
        _calendar.Gate.SetResult();
        await placing;

        Assert.Equal("Masterplan", detail.Title);
        Assert.Empty(detail.MoreEpisodesRow);
        Assert.False(detail.HasMoreEpisodes);
    }

    [Fact]
    public async Task LeavingThePage_StopsThePlacing()
    {
        _fixture.Episode().Get(SeriesEpisodesPath(SeriesId, 3), Ok(Listing));
        _calendar.Airs(3, "The Heist", "2026-09-15");
        _calendar.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Guard(2);
        var detail = _fixture.Create();
        await detail.LoadCommand.ExecuteAsync(EpisodeId);
        var placing = detail.Placing;

        detail.Deactivate();
        await placing;
        _calendar.Gate.SetResult();

        Assert.Equal(4, detail.MoreEpisodesRow.Count);
        Assert.Empty(detail.MoreEpisodesRow.OfType<DetailEpisodePlaceholder>());
        AssertQuiet();
    }

    [Fact]
    public async Task NoPlacesComeIntoTheRow_OfASeasonPage()
    {
        _fixture.Season().Get(SeasonItemEpisodesPath(Season3Id), Ok(Listing));
        _calendar.Airs(3, "The Heist", "2026-09-15");
        _fixture.Guard(2);

        var detail = await LoadAsync(Season3Id);

        Assert.Single(detail.EpisodeGrid.OfType<DetailEpisodePlaceholder>());
        Assert.False(detail.HasMoreEpisodes);
        Assert.Empty(detail.MoreEpisodesRow);
    }

    // The page loaded, its places included, without a warning on the way.
    private async Task<ItemDetailViewModel> LoadAsync(string id)
    {
        var detail = await _fixture.LoadAsync(id);
        AssertQuiet();
        return detail;
    }

    private void AssertQuiet() =>
        Assert.DoesNotContain(_fixture.Logs.Lines, line => line.StartsWith("Warning ItemDetailViewModel:", StringComparison.Ordinal));

    // What the row shows on each card: an episode's card title, a place's line.
    private static List<string> Titles(ItemDetailViewModel detail) =>
        [.. detail.MoreEpisodesRow.Select(entry => entry switch
        {
            MediaCard card => card.Title,
            DetailEpisodePlaceholder place => place.Title,
            _ => "",
        })];
}
