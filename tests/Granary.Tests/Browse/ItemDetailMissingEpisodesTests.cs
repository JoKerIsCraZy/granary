using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Detail;
using Granary.Tests.Profiles;
using static Granary.Tests.Browse.ItemDetailFixture;
using static Granary.Tests.Browse.ItemDetailPayloads;

namespace Granary.Tests.Browse;

/// <summary>
/// A season's grid places the episodes its listing lacks: missing between those it has or aired after its last, and those
/// still to air, named by the calendar. The clock stands on Thursday, 1 October 2026, at noon in UTC.
/// </summary>
public sealed class ItemDetailMissingEpisodesTests
{
    private const string CalendarPath = "api/v2/calendar";
    private const string SpecialsId = "season-96677-0";
    private const string OtherSeasonId = "season-tmdb-5-1";

    private const string Specials = """
        {
          "content_id": "season-96677-0", "type": "season", "title": "Specials", "status": "matched", "genres": [], "keywords": [],
          "series_id": "series-tmdb-96677", "series_title": "Lupin", "season_number": 0, "episode_count": 2,
          "versions": [], "subtitles": []
        }
        """;

    /// <summary>Season 1 of another series.</summary>
    private const string OtherSeason = """
        {
          "content_id": "season-tmdb-5-1", "type": "season", "title": "Season 1", "status": "matched", "genres": [], "keywords": [],
          "series_id": "series-tmdb-5", "series_title": "Reacher", "season_number": 1, "episode_count": 1,
          "versions": [], "subtitles": []
        }
        """;

    private static readonly (string, string)[] TodaysWindows = [("2026-10-01", "2026-10-31"), ("2026-11-01", "2026-12-01")];

    private readonly ItemDetailFixture _fixture = new();
    private readonly FakeCalendar _calendar = new();

    public ItemDetailMissingEpisodesTests()
    {
        _fixture.Airings = new SeriesAirings(new CalendarApi(_fixture.Silo.Http), _fixture.Session, _fixture.Clock);
        _fixture.Spans = new EpisodeSpans(new CatalogApi(_fixture.Silo.Http), _fixture.Session, _fixture.Clock);
        _fixture.Silo.OnUrl(HttpMethod.Get, CalendarPath, _calendar.RespondAsync);
    }

    [Fact]
    public async Task AGapBetweenEpisodes_ShowsTheMissingEpisode_AsTheCalendarNamesIt()
    {
        Season(Episodes((1, "2026-09-01"), (2, "2026-09-08"), (4, "2026-09-22"), (5, "2026-09-29")));
        _calendar.Airs(3, "The Heist", "2026-09-15");
        Guard(2);

        var detail = await LoadAsync(Season3Id);

        Assert.Equal(["S03E01 · Chapter 1", "S03E02 · Chapter 2", "S03E03 · The Heist", "S03E04 · Chapter 4", "S03E05 · Chapter 5"], Titles(detail));
        var missing = Assert.Single(detail.EpisodeGrid.OfType<DetailEpisodePlaceholder>());
        Assert.True(missing.IsMissing);
        Assert.Equal("15 Sep 2026", missing.Detail);
        Assert.Equal("Episode 3, missing", missing.AccessibleName);
        Assert.Equal(4, detail.Episodes.Count);
        // The gap's window, a day wider at each end, and the days after the last episode up to yesterday are one request;
        // then today's two.
        Assert.Equal([("2026-09-07", "2026-09-30"), .. TodaysWindows], Windows());
    }

    [Fact]
    public async Task AGapTheCalendarDoesNotKnow_ShowsTheEpisodesNumber_WithoutADate()
    {
        Season(Episodes((1, "2026-09-01"), (2, "2026-09-08"), (4, "2026-09-22"), (5, "2026-09-29")));
        Guard(2);

        var detail = await LoadAsync(Season3Id);

        var missing = Assert.Single(detail.EpisodeGrid.OfType<DetailEpisodePlaceholder>());
        Assert.Equal("Episode 3", missing.Title);
        Assert.True(missing.IsMissing);
        Assert.Null(missing.Detail);
        Assert.Equal("Episode 3, missing", missing.AccessibleName);
        Assert.Equal(missing, detail.EpisodeGrid[2]);
    }

    [Fact]
    public async Task NeighboursMoreThan31DaysApart_AskNothingForTheGap()
    {
        Season(Episodes((1, "2026-07-01"), (2, "2026-08-01"), (4, "2026-09-22")));
        _calendar.Airs(3, "The Heist", "2026-08-20");
        Guard(2);

        var detail = await LoadAsync(Season3Id);

        var missing = Assert.Single(detail.EpisodeGrid.OfType<DetailEpisodePlaceholder>());
        Assert.Equal("Episode 3", missing.Title);
        Assert.Null(missing.Detail);
        Assert.Equal([("2026-09-22", "2026-09-30"), .. TodaysWindows], Windows());
    }

    [Fact]
    public async Task AGapsEpisode_ListedOnTheViewersNextDay_IsStillFound_AndShowsThatDay()
    {
        // Aired late in the evening where it was made: the calendar lists it under the viewer's next day.
        Season(Episodes((4, "2026-08-01"), (6, "2026-08-08"), (7, "2026-09-20")));
        _calendar.Airs(5, "Late Night", "2026-08-08", local: "2026-08-09");
        Guard(4);

        var detail = await LoadAsync(Season3Id);

        var missing = Assert.Single(detail.EpisodeGrid.OfType<DetailEpisodePlaceholder>());
        Assert.Equal("S03E05 · Late Night", missing.Title);
        Assert.Equal("9 Aug 2026", missing.Detail);
        Assert.Contains(("2026-07-31", "2026-08-09"), Windows());
    }

    [Fact]
    public async Task AGapsEpisode_StillToAir_IsUpcoming()
    {
        // The listing has a file of episode 3 ahead of its air date.
        Season(Episodes((1, "2026-09-24"), (3, "2026-10-08")));
        _calendar.Airs(2, "Chapter 2", "2026-10-06");

        var detail = await LoadAsync(Season3Id);

        var upcoming = Assert.Single(detail.EpisodeGrid.OfType<DetailEpisodePlaceholder>());
        Assert.False(upcoming.IsMissing);
        Assert.Equal("Airs Tue, Oct 6", upcoming.Detail);
        Assert.Equal("Episode 2, airs October 6", upcoming.AccessibleName);
        Assert.Equal(upcoming, detail.EpisodeGrid[1]);
    }

    [Fact]
    public async Task EpisodesAiredAfterTheLastOne_AreMissing_AndOneLaterToday_IsUpcoming()
    {
        Season(Episodes((1, "2026-09-06"), (2, "2026-09-13"), (3, "2026-09-20")));
        _calendar.Airs(4, "Chapter 4", "2026-09-27")
            .Airs(5, "Chapter 5", "2026-10-01", at: "2026-10-01T09:00:00Z")
            .Airs(6, "Chapter 6", "2026-10-01", at: "2026-10-01T21:00:00Z");
        Guard(3);

        var detail = await LoadAsync(Season3Id);

        Assert.Equal(["S03E01 · Chapter 1", "S03E02 · Chapter 2", "S03E03 · Chapter 3", "S03E04 · Chapter 4", "S03E05 · Chapter 5", "S03E06 · Chapter 6"],
            Titles(detail));
        var places = detail.EpisodeGrid.OfType<DetailEpisodePlaceholder>().ToList();
        Assert.Equal([true, true, false], places.Select(p => p.IsMissing));
        Assert.Equal(["27 Sep 2026", "1 Oct 2026", "Airs Thu, Oct 1"], places.Select(p => p.Detail));
        Assert.Equal(["Episode 4, missing", "Episode 5, missing", "Episode 6, airs October 1"], places.Select(p => p.AccessibleName));
        Assert.Equal([("2026-09-20", "2026-09-30"), .. TodaysWindows], Windows());
    }

    [Fact]
    public async Task AZoneWithoutAName_AsksByTheDaysTheServerReckonsIn()
    {
        // Half past one on 2 October here is still 1 October in UTC, the days the server uses without a zone name.
        _fixture.Clock.Zone = TimeZoneInfo.CreateCustomTimeZone("Granary half hour", TimeSpan.FromMinutes(330), "Half hour", "Half hour");
        _fixture.Clock.Advance(TimeSpan.FromHours(8));
        Season(Episodes((1, "2026-09-17"), (2, "2026-09-24")));

        await LoadAsync(Season3Id);

        Assert.Equal([("2026-09-24", "2026-09-30"), .. TodaysWindows], Windows());
        Assert.All(_fixture.Silo.SentTo(HttpMethod.Get, CalendarPath), r => Assert.DoesNotContain("timezone=", r.Uri.Query, StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpcomingEpisodes_FollowTheLastOne_InOrder_WithTheDayTheyAir()
    {
        Season(Episodes((1, "2026-09-17"), (2, "2026-09-24")));
        _calendar.Airs(4, "Chapter 4", "2026-11-19", at: "2026-11-19T21:00:00Z")
            .Airs(3, "Chapter 3", "2026-10-08")
            .Airs(1, "Another season", "2026-10-15", season: 4)
            .Airs(5, "Another series", "2026-10-10", series: "series-tmdb-7")
            .Airs(6, "Too far ahead", "2026-12-10");

        var detail = await LoadAsync(Season3Id);

        Assert.Equal(["S03E01 · Chapter 1", "S03E02 · Chapter 2", "S03E03 · Chapter 3", "S03E04 · Chapter 4"], Titles(detail));
        var upcoming = detail.EpisodeGrid.OfType<DetailEpisodePlaceholder>().ToList();
        Assert.All(upcoming, place => Assert.False(place.IsMissing));
        Assert.Equal(["Airs Thu, Oct 8", "Airs Thu, Nov 19"], upcoming.Select(p => p.Detail));
        Assert.Equal(["Episode 3, airs October 8", "Episode 4, airs November 19"], upcoming.Select(p => p.AccessibleName));
    }

    [Fact]
    public async Task ASeasonWithNoListedEpisodes_ShowsTheEpisodesStillToAir()
    {
        Season(NoEpisodes);
        _calendar.Airs(1, "Chapter 1", "2026-10-08").Airs(2, "Chapter 2", "2026-10-15");

        var detail = await LoadAsync(Season3Id);

        Assert.True(detail.ShowsNoEpisodes);
        Assert.Equal(["S03E01 · Chapter 1", "S03E02 · Chapter 2"], Titles(detail));
        Assert.Equal(TodaysWindows, Windows());
    }

    [Fact]
    public async Task EpisodesSharingADoubleEpisodesFile_AreNoGap()
    {
        Season($$"""
            { "items": [
              {{Episode(4, "2026-09-08")}},
              {{Episode(5, "2026-09-15", fileId: "955")}},
              {{Episode(6, "2026-09-15", fileId: "955")}},
              {{Episode(7, "2026-09-22")}}
            ] }
            """);
        _calendar.Airs(5, "Part One", "2026-09-15").Airs(6, "Part Two", "2026-09-15");

        var detail = await LoadAsync(Season3Id);

        Assert.Empty(detail.EpisodeGrid.OfType<DetailEpisodePlaceholder>());
        Assert.Equal(4, detail.EpisodeGrid.Count);
    }

    [Fact]
    public async Task AServerThatListsBothHalvesOfADoubleEpisode_IsNotAskedAboutAGap()
    {
        Season($$"""
            { "items": [
              {{Episode(1, "2026-09-01")}},
              {{Episode(2, "2026-09-08", fileId: "955")}},
              {{Episode(3, "2026-09-08", fileId: "955")}},
              {{Episode(5, "2026-09-22")}}
            ] }
            """);

        var detail = await LoadAsync(Season3Id);

        Assert.Equal("Episode 4", Assert.Single(detail.EpisodeGrid.OfType<DetailEpisodePlaceholder>()).Title);
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Get, ItemPath(EpisodeIdOf(3))));
    }

    [Fact]
    public async Task AnEpisodeADoubleEpisodesFileHolds_IsNotMissing_WhereTheListingLeavesItOut()
    {
        // A server that lists only the first episode of a multi-episode file: its versions name the rest.
        Season(Episodes((4, "2026-09-08"), (5, "2026-09-15"), (7, "2026-09-22")));
        _calendar.Airs(6, "Part Two", "2026-09-15").Airs(8, "Finale, Part Two", "2026-09-22");
        Guard(5, spansTo: 6);
        Guard(7, spansTo: 8);

        var detail = await LoadAsync(Season3Id);
        await LoadAsync(Season3Id);

        Assert.Empty(detail.EpisodeGrid.OfType<DetailEpisodePlaceholder>());
        // Each episode's files are read once, and kept for the page opened again.
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, ItemPath(EpisodeIdOf(5))));
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Get, ItemPath(EpisodeIdOf(7))));
    }

    [Fact]
    public async Task AnEpisodeWhoseFilesCannotBeRead_KeepsOnlyItsOwnPlacesOut()
    {
        Season(Episodes((1, "2026-09-01"), (2, "2026-09-08"), (4, "2026-09-15"), (5, "2026-09-22"), (7, "2026-09-29")));
        _calendar.Airs(3, "Chapter 3", "2026-09-12").Airs(6, "Chapter 6", "2026-09-26");
        _fixture.Get(ItemPath(EpisodeIdOf(2)), ScriptedSilo.Problem(500, "internal_error"));
        Guard(5);

        var detail = await _fixture.LoadAsync(Season3Id);

        // Episode 3 might be the second half of episode 2's file, so it stays out; episode 6 is missing.
        Assert.Equal("S03E06 · Chapter 6", Assert.Single(detail.EpisodeGrid.OfType<DetailEpisodePlaceholder>()).Title);
        Assert.Contains(_fixture.Logs.Lines, line => line.StartsWith("Warning ItemDetailViewModel:", StringComparison.Ordinal)
            && line.Contains("500", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGapOf500Numbers_ShowsOnlyTheEpisodesTheCalendarNames()
    {
        // Numbered on from the season before, or a stray number: not 500 missing episodes.
        Season(Episodes((11, "2026-09-01"), (12, "2026-09-08"), (513, "2026-09-15")));
        _calendar.Airs(13, "Chapter 13", "2026-09-10").Airs(14, "Chapter 14", "2026-09-12");
        Guard(12);

        var detail = await LoadAsync(Season3Id);

        Assert.Equal(["S03E13 · Chapter 13", "S03E14 · Chapter 14"], detail.EpisodeGrid.OfType<DetailEpisodePlaceholder>().Select(p => p.Title));
        Assert.Equal(5, detail.EpisodeGrid.Count);
    }

    [Fact]
    public async Task ASeason_HasAtMost50Places()
    {
        int[] odd = [.. Enumerable.Range(0, 61).Select(i => 2 * i + 1)];
        Season(Episodes([.. odd.Select(n => (n, "2026-09-01"))]));
        foreach (int number in odd.Where(n => n < 100))
        {
            Guard(number);
        }

        var detail = await LoadAsync(Season3Id);

        var places = detail.EpisodeGrid.OfType<DetailEpisodePlaceholder>().ToList();
        Assert.Equal(50, places.Count);
        Assert.Equal(Enumerable.Range(1, 50).Select(i => 2 * i), places.Select(p => p.EpisodeNumber));
        // Only the episodes before the places shown are read.
        Assert.Empty(_fixture.Silo.SentTo(HttpMethod.Get, ItemPath(EpisodeIdOf(101))));
    }

    [Fact]
    public async Task ManyGaps_AskAtMostFourWindows_AndReadThreeEpisodesAtATime()
    {
        int[] listed = [1, 3, 5, 7, 9, 11, 13, 15, 17];
        Season(Episodes([.. listed.Select((n, i) => (n, new DateOnly(2026, 6, 1).AddDays(10 * i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))]));
        // Each read waits until the test lets it answer; the reads started, in order.
        var gates = listed[..^1].ToDictionary(n => n, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        System.Collections.Concurrent.ConcurrentQueue<int> started = new();
        foreach (var (number, gate) in gates)
        {
            _fixture.Get(ItemPath(EpisodeIdOf(number)), async () =>
            {
                started.Enqueue(number);
                await gate.Task;
                return await Ok(EpisodeItem(number, 3, null))();
            });
        }
        var detail = _fixture.Create();

        await detail.LoadCommand.ExecuteAsync(Season3Id);
        await Auth.AuthPipeline.Eventually(() => started.Count >= 3);
        Assert.Equal(3, started.Count);
        gates[started.First()].SetResult();
        await Auth.AuthPipeline.Eventually(() => started.Count >= 4);

        // One answered, one more started, and no other.
        Assert.Equal(4, started.Count);
        foreach (var gate in gates.Values)
        {
            gate.TrySetResult();
        }
        await detail.Placing;
        Assert.Equal(8, started.Count);
        Assert.Equal(8, detail.EpisodeGrid.OfType<DetailEpisodePlaceholder>().Count());
        // Today's two windows and the latest two of the others.
        Assert.Equal([("2026-07-30", "2026-08-21"), ("2026-08-20", "2026-09-19"), .. TodaysWindows], Windows());
        AssertQuiet();
    }

    [Fact]
    public async Task TheCalendarsAnswers_AreKeptForAFewMinutes()
    {
        Season(Episodes((1, "2026-09-17"), (2, "2026-09-24")));
        _calendar.Airs(3, "Chapter 3", "2026-10-08");

        await LoadAsync(Season3Id);
        int asked = Windows().Count;
        var again = await LoadAsync(Season3Id);

        Assert.Equal(3, asked);
        Assert.Equal(asked, Windows().Count);
        Assert.Equal("S03E03 · Chapter 3", Assert.Single(again.EpisodeGrid.OfType<DetailEpisodePlaceholder>()).Title);

        _fixture.Clock.Advance(SeriesAirings.KeepFor);
        await LoadAsync(Season3Id);

        Assert.Equal(2 * asked, Windows().Count);
    }

    [Fact]
    public async Task TheCalendarsAnswers_ServeEverySeries_ButNotAnotherProfile()
    {
        Season(Episodes((1, "2026-09-17"), (2, "2026-09-24")));
        _fixture.Get(ItemPath(OtherSeasonId), Ok(OtherSeason)).Get(SeasonItemEpisodesPath(OtherSeasonId), Ok(Episodes(1, (1, "2026-09-10"))));
        _calendar.Airs(2, "Second", "2026-10-15", season: 1, series: "series-tmdb-5");

        await LoadAsync(Season3Id);
        var other = await LoadAsync(OtherSeasonId);

        // Today's windows were asked once for both series; the other season added only its own month after its last episode.
        Assert.Equal([("2026-09-10", "2026-09-30"), ("2026-09-24", "2026-09-30"), .. TodaysWindows], Windows());
        Assert.Equal("S01E02 · Second", Assert.Single(other.EpisodeGrid.OfType<DetailEpisodePlaceholder>()).Title);

        _fixture.Session.ProfileId = "8";
        await LoadAsync(Season3Id);

        Assert.Equal(7, Windows().Count);
    }

    [Fact]
    public async Task TwoPagesAskingTheSameWindows_ShareTheirRequests()
    {
        Season(Episodes((1, "2026-09-17"), (2, "2026-09-24")));
        _calendar.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = _fixture.Create();
        var second = _fixture.Create();

        await first.LoadCommand.ExecuteAsync(Season3Id);
        await second.LoadCommand.ExecuteAsync(Season3Id);
        _calendar.Gate.SetResult();
        await Task.WhenAll(first.Placing, second.Placing);

        Assert.Equal(3, Windows().Count);
        AssertQuiet();
    }

    [Fact]
    public async Task AProfileOrSessionChange_ForgetsWhatWasKept()
    {
        Season(Episodes((4, "2026-09-08"), (5, "2026-09-15"), (7, "2026-09-22")));
        Guard(5);

        await LoadAsync(Season3Id);
        _fixture.Session.ProfileId = "8";
        _fixture.Session.RaiseChanged();
        _fixture.Session.ProfileId = "7";
        _fixture.Session.RaiseChanged();
        await LoadAsync(Season3Id);

        // Back on the first profile, the calendar and the episode's files are read again.
        Assert.Equal(6, Windows().Count);
        Assert.Equal(2, _fixture.Silo.SentTo(HttpMethod.Get, ItemPath(EpisodeIdOf(5))).Count);
    }

    [Fact]
    public async Task AnAnswerReadWhileAnotherProfileActs_IsNotKeptForTheFirst()
    {
        Season(Episodes((1, "2026-09-17"), (2, "2026-09-24")));
        _calendar.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detail = _fixture.Create();
        await detail.LoadCommand.ExecuteAsync(Season3Id);

        // The switch is under way: the requests may have gone out as profile 8, which has not said it changed yet.
        _fixture.Session.ProfileId = "8";
        _calendar.Gate.SetResult();
        await detail.Placing;
        _fixture.Session.ProfileId = "7";
        await LoadAsync(Season3Id);

        Assert.Equal(6, Windows().Count);
    }

    [Fact]
    public async Task ACalendarThatNeverAnswers_IsGivenUpAfter30Seconds()
    {
        Season(Episodes((1, "2026-09-17"), (2, "2026-09-24")));
        _calendar.Airs(3, "Chapter 3", "2026-10-08");
        _calendar.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detail = _fixture.Create();
        await detail.LoadCommand.ExecuteAsync(Season3Id);

        _fixture.Clock.Advance(SeriesAirings.GiveUpAfter);
        await detail.Placing;

        Assert.Equal(2, detail.EpisodeGrid.Count);
        Assert.Contains(_fixture.Logs.Lines, line => line.StartsWith("Warning ItemDetailViewModel:", StringComparison.Ordinal)
            && line.Contains("timeout", StringComparison.Ordinal));
        // Nothing stays on its way: the next page asks again, and is answered.
        _calendar.Gate.SetResult();
        var again = await _fixture.LoadAsync(Season3Id);
        Assert.Equal(6, Windows().Count);
        Assert.Single(again.EpisodeGrid.OfType<DetailEpisodePlaceholder>());
    }

    [Fact]
    public async Task MarkingTheSeasonWatched_WhileThePlacesAreOnTheirWay_LeavesOneConsistentGrid()
    {
        Season(Episodes((1, "2026-09-01"), (2, "2026-09-08"), (4, "2026-09-22")));
        _calendar.Airs(3, "The Heist", "2026-09-15");
        _calendar.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guard(2);
        _fixture.Silo.On(HttpMethod.Post, "api/v2/watched/" + Season3Id, ScriptedSilo.NoContent());
        var detail = _fixture.Create();
        await detail.LoadCommand.ExecuteAsync(Season3Id);

        await detail.ToggleWatchedCommand.ExecuteAsync(null);
        _calendar.Gate.SetResult();
        await detail.Placing;

        Assert.Equal(["S03E01 · Chapter 1", "S03E02 · Chapter 2", "S03E03 · The Heist", "S03E04 · Chapter 4"], Titles(detail));
        Assert.All(detail.Episodes, episode => Assert.True(episode.Card.IsWatched));
        // The grid holds the very cards Episodes has, watched, and the place among them.
        Assert.Same(detail.Episodes[0], detail.EpisodeGrid[0]);
        Assert.Same(detail.Episodes[1], detail.EpisodeGrid[1]);
        Assert.Same(detail.Episodes[2], detail.EpisodeGrid[3]);
        AssertQuiet();
    }

    [Fact]
    public async Task TheLoad_DoesNotWaitForTheCalendar_AndThePlacesComeInAmongTheSameCards()
    {
        Season(Episodes((1, "2026-09-01"), (2, "2026-09-08"), (4, "2026-09-22")));
        _calendar.Airs(3, "The Heist", "2026-09-15");
        _calendar.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guard(2);
        var detail = _fixture.Create();
        List<NotifyCollectionChangedAction> changes = [];
        detail.EpisodeGrid.CollectionChanged += (_, e) => changes.Add(e.Action);

        await detail.LoadCommand.ExecuteAsync(Season3Id);

        Assert.False(detail.Placing.IsCompleted);
        Assert.Equal(3, detail.EpisodeGrid.Count);
        var cards = detail.EpisodeGrid.ToList();
        changes.Clear();
        _calendar.Gate.SetResult();
        await detail.Placing;
        // Only the place came in: every card stayed where it was, the same entry.
        Assert.Equal([NotifyCollectionChangedAction.Add], changes);
        Assert.Equal("S03E03 · The Heist", Assert.IsType<DetailEpisodePlaceholder>(detail.EpisodeGrid[2]).Title);
        Assert.Same(cards[0], detail.EpisodeGrid[0]);
        Assert.Same(cards[1], detail.EpisodeGrid[1]);
        Assert.Same(cards[2], detail.EpisodeGrid[3]);
    }

    [Fact]
    public async Task ThePlaces_AreNotEpisodes_SoNothingPlaysMarksOrBlursThem()
    {
        Season(Episodes((1, "2026-09-01"), (2, "2026-09-08"), (4, "2026-09-22")));
        _calendar.Airs(3, "The Heist", "2026-09-15");
        Guard(2);
        _fixture.Silo.On(HttpMethod.Post, "api/v2/watched/" + Season3Id, ScriptedSilo.NoContent());
        var detail = await LoadAsync(Season3Id);
        var grid = detail.EpisodeGrid;
        var place = Assert.Single(grid.OfType<DetailEpisodePlaceholder>());
        List<NotifyCollectionChangedAction> changes = [];
        grid.CollectionChanged += (_, e) => changes.Add(e.Action);

        // Play, the watched toggles, the spoiler blur and opening a card work on Episodes, which holds only the listing's; a
        // place has no card, id or image for them to take.
        Assert.Equal(EpisodeId, detail.PlayTargetId);
        Assert.Equal([EpisodeIdOf(1), EpisodeIdOf(2), EpisodeIdOf(4)], detail.Episodes.Select(e => e.Id));
        await detail.ToggleWatchedCommand.ExecuteAsync(null);

        Assert.All(detail.Episodes, episode => Assert.True(episode.Card.IsWatched));
        // Each changed card is replaced where it is; the place stays as it was.
        Assert.Same(grid, detail.EpisodeGrid);
        Assert.Equal([NotifyCollectionChangedAction.Replace, NotifyCollectionChangedAction.Replace, NotifyCollectionChangedAction.Replace], changes);
        Assert.Same(place, detail.EpisodeGrid[2]);
        Assert.Single(_fixture.Silo.SentTo(HttpMethod.Post, "api/v2/watched/" + Season3Id));
    }

    [Fact]
    public async Task AnotherTitleLoadedMeanwhile_GetsNoneOfTheSeasonsPlaces()
    {
        Season(Episodes((1, "2026-09-01"), (2, "2026-09-08"), (4, "2026-09-22")));
        _calendar.Airs(3, "The Heist", "2026-09-15");
        _calendar.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guard(2);
        _fixture.Movie();
        var detail = _fixture.Create();

        await detail.LoadCommand.ExecuteAsync(Season3Id);
        var placing = detail.Placing;
        await detail.LoadCommand.ExecuteAsync(MovieId);
        _calendar.Gate.SetResult();
        await placing;

        Assert.Equal("Masterplan", detail.Title);
        Assert.Empty(detail.EpisodeGrid);
    }

    [Fact]
    public async Task LeavingThePage_StopsThePlacing()
    {
        Season(Episodes((1, "2026-09-01"), (2, "2026-09-08"), (4, "2026-09-22")));
        _calendar.Airs(3, "The Heist", "2026-09-15");
        _calendar.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guard(2);
        var detail = _fixture.Create();
        await detail.LoadCommand.ExecuteAsync(Season3Id);
        var placing = detail.Placing;

        detail.LoadCommand.Cancel();
        detail.Deactivate();
        await placing;
        _calendar.Gate.SetResult();

        Assert.Empty(detail.EpisodeGrid.OfType<DetailEpisodePlaceholder>());
        Assert.Equal(3, detail.EpisodeGrid.Count);
        Assert.DoesNotContain(_fixture.Logs.Lines, line => line.StartsWith("Warning ItemDetailViewModel:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EpisodeZero_AndAListingOutOfOrder_KeepTheServersOrder()
    {
        Season(Episodes((0, "2026-08-25"), (2, "2026-09-08"), (1, "2026-09-01"), (4, "2026-09-22")));
        Guard(2);

        var detail = await LoadAsync(Season3Id);

        // Episode 0 is no gap's neighbour; the place goes before the first episode numbered after it.
        Assert.Equal(["S03E00 · Chapter 0", "S03E02 · Chapter 2", "S03E01 · Chapter 1", "Episode 3", "S03E04 · Chapter 4"], Titles(detail));
    }

    [Fact]
    public async Task AFailedCalendarRequest_ShowsTheSeasonAsItIs()
    {
        Season(Episodes((1, "2026-09-01"), (2, "2026-09-08"), (4, "2026-09-22")));
        _calendar.Fails = true;

        var detail = await _fixture.LoadAsync(Season3Id);

        Assert.Equal(["S03E01 · Chapter 1", "S03E02 · Chapter 2", "S03E04 · Chapter 4"], Titles(detail));
        Assert.Equal(3, detail.Episodes.Count);
        Assert.False(detail.HasActionError);
        Assert.Contains(_fixture.Logs.Lines, line => line.StartsWith("Warning ItemDetailViewModel:", StringComparison.Ordinal)
            && line.Contains("503", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASingleSeasonSeries_PlacesItsMissingEpisodes_Too()
    {
        _fixture.Series(SingleSeason).Get(SeasonItemEpisodesPath("season-96677-1"),
            Ok(Episodes(1, (1, "2026-09-01"), (2, "2026-09-08"), (4, "2026-09-22"))));
        _calendar.Airs(3, "The Heist", "2026-09-15", season: 1);
        Guard(2, season: 1);

        var detail = await LoadAsync(SeriesId);

        Assert.Equal(["S01E01 · Chapter 1", "S01E02 · Chapter 2", "S01E03 · The Heist", "S01E04 · Chapter 4"], Titles(detail));
    }

    [Fact]
    public async Task NothingIsPlaced_OnAMovie_OnAnEpisodesMoreEpisodes_OrAmongTheSpecials()
    {
        _fixture.Movie();
        await LoadAsync(MovieId);
        _fixture.Episode().Get(SeriesEpisodesPath(SeriesId, 3), Ok(Episodes((1, "2026-09-01"), (3, "2026-09-15"))));
        var episode = await LoadAsync(EpisodeId);
        _fixture.Get(ItemPath(SpecialsId), Ok(Specials)).Get(SeasonItemEpisodesPath(SpecialsId), Ok(Episodes(0, (1, "2026-09-01"), (3, "2026-09-15"))));
        var specials = await LoadAsync(SpecialsId);

        Assert.Equal([EpisodeIdOf(1), EpisodeIdOf(3)], episode.MoreEpisodes.Select(c => c.Id));
        Assert.Empty(episode.EpisodeGrid);
        Assert.Equal(2, specials.EpisodeGrid.Count);
        Assert.Empty(specials.EpisodeGrid.OfType<DetailEpisodePlaceholder>());
        // The calendar leaves the specials out, so their season asks it nothing.
        Assert.Empty(Windows());
    }

    // The page loaded, its places included, without a warning on the way: an unexpected request or a mistake of the placing
    // would log one.
    private async Task<ItemDetailViewModel> LoadAsync(string id)
    {
        var detail = await _fixture.LoadAsync(id);
        AssertQuiet();
        return detail;
    }

    private void AssertQuiet() =>
        Assert.DoesNotContain(_fixture.Logs.Lines, line => line.StartsWith("Warning ItemDetailViewModel:", StringComparison.Ordinal));

    private void Season(string episodes) =>
        _fixture.Get(ItemPath(Season3Id), Ok(ItemDetailPayloads.Season)).Get(SeasonItemEpisodesPath(Season3Id), Ok(episodes));

    // The read of an episode's files that tells a double episode, made only on a server that lists no double episode's halves.
    private void Guard(int number, int? spansTo = null, int season = 3) =>
        _fixture.Get(ItemPath(EpisodeIdOf(number, season)), Ok(EpisodeItem(number, season, spansTo)));

    private static string EpisodeIdOf(int number, int season = 3) => $"episode-tmdb-96677-{season}-{number}";

    private static string Episodes(params (int Number, string AirDate)[] episodes) => Episodes(3, episodes);

    private static string Episodes(int season, params (int Number, string AirDate)[] episodes) =>
        $$"""{ "items": [{{string.Join(",", episodes.Select(e => Episode(e.Number, e.AirDate, season: season)))}}] }""";

    private static string Episode(int number, string airDate, string? fileId = null, int season = 3) => $$"""
        { "content_id": "{{EpisodeIdOf(number, season)}}", "season_number": {{season}}, "episode_number": {{number}}, "title": "Chapter {{number}}",
          "air_date": "{{airDate}}", "runtime": 50, "files": [{ "file_id": "{{fileId ?? $"9{season}{number}"}}", "resolution": "1080p", "hdr": false, "file_size": 1 }] }
        """;

    private static string EpisodeItem(int number, int season, int? spansTo)
    {
        string span = spansTo is { } end ? $", \"multi_episode_start\": {number}, \"multi_episode_end\": {end}" : "";
        return $$"""
            {
              "content_id": "{{EpisodeIdOf(number, season)}}", "type": "episode", "title": "Chapter {{number}}", "status": "matched",
              "genres": [], "keywords": [], "series_id": "series-tmdb-96677", "season_number": {{season}}, "episode_number": {{number}},
              "versions": [{ "file_id": "9{{season}}{{number}}", "resolution": "1080p", "codec_video": "h264", "codec_audio": "eac3", "container": "mkv",
                "hdr": false, "file_size": 1, "duration": 3000, "bitrate": 6000{{span}} }],
              "videos": [], "subtitles": []
            }
            """;
    }

    // What the grid shows on each card: an episode's card title, a place's line.
    private static List<string> Titles(ItemDetailViewModel detail) =>
        [.. detail.EpisodeGrid.Select(entry => entry switch
        {
            DetailEpisode episode => episode.Card.Title,
            DetailEpisodePlaceholder place => place.Title,
            _ => "",
        })];

    // The calendar windows asked, by their start.
    private List<(string Start, string End)> Windows() =>
        [.. _fixture.Silo.SentTo(HttpMethod.Get, CalendarPath).Select(r => (Start: Query(r.Uri, "start"), End: Query(r.Uri, "end")))
            .OrderBy(w => w.Start, StringComparer.Ordinal)];

    private static string Query(Uri uri, string name) =>
        uri.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2)).Single(pair => pair[0] == name)[1];

    /// <summary>
    /// The server's calendar: each window answered with the airings listed under its days, as the server trims them to the
    /// window it was asked for by the viewer's day.
    /// </summary>
    private sealed class FakeCalendar
    {
        private readonly List<Airing> _airings = [];

        /// <summary>Answers with 503 instead.</summary>
        public bool Fails { get; set; }

        /// <summary>Held until the test sets it, when set.</summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <param name="local">The viewer's day it is listed under, when it is not <paramref name="date"/>.</param>
        public FakeCalendar Airs(int episode, string title, string date, string? at = null, int season = 3, string series = SeriesId,
            string? local = null)
        {
            _airings.Add(new Airing(series, season, episode, title, date, local ?? date, at));
            return this;
        }

        public async Task<HttpResponseMessage> RespondAsync(Uri uri, CancellationToken cancellationToken)
        {
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }
            if (Fails)
            {
                return await ScriptedSilo.Problem(503, "dependency_unavailable")();
            }
            var start = DateOnly.ParseExact(Query(uri, "start"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var end = DateOnly.ParseExact(Query(uri, "end"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var days = _airings.Where(a => a.Day >= start && a.Day <= end).GroupBy(a => a.Local).OrderBy(day => day.Key, StringComparer.Ordinal)
                .Select(day => $$"""{ "date": "{{day.Key}}", "items": [{{string.Join(",", day.Select(a => a.Json))}}] }""");
            return await ScriptedSilo.Json(HttpStatusCode.OK, $$"""{ "events": [{{string.Join(",", days)}}] }""")();
        }

        private sealed record Airing(string Series, int Season, int Episode, string Title, string Date, string Local, string? At)
        {
            public DateOnly Day => DateOnly.ParseExact(Local, "yyyy-MM-dd", CultureInfo.InvariantCulture);

            public string Json => $$"""
                {
                  "content_id": "episode-{{Series}}-{{Season}}-{{Episode}}", "type": "episode", "title": "Lupin", "series_id": "{{Series}}",
                  "season_number": {{Season}}, "episode_number": {{Episode}}, "episode_title": "{{Title}}", "air_date": "{{Date}}",
                  "local_air_date": "{{Local}}"{{(At is null ? "" : $", \"air_at\": \"{At}\"")}}, "badges": [], "watched": false
                }
                """;
        }
    }
}
