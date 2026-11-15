using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Detail;
using static Granary.Tests.Browse.ItemDetailFixture;
using static Granary.Tests.Browse.ItemDetailPayloads;

namespace Granary.Tests.Browse;

/// <summary>
/// The hero of a series', season's and episode's page names when the series' next episode airs, from the calendar's next 62
/// days. The clock stands on Thursday, 1 October 2026, at noon in UTC.
/// </summary>
public sealed class ItemDetailNextAiringTests
{
    private static readonly (string, string)[] TodaysWindows = [("2026-10-01", "2026-10-31"), ("2026-11-01", "2026-12-01")];

    private readonly ItemDetailFixture _fixture = new();
    private readonly FakeCalendar _calendar = new();

    public ItemDetailNextAiringTests()
    {
        _fixture.Airings = new SeriesAirings(new CalendarApi(_fixture.Silo.Http), _fixture.Session, _fixture.Clock);
        _fixture.Spans = new EpisodeSpans(new CatalogApi(_fixture.Silo.Http), _fixture.Session, _fixture.Clock);
        _fixture.Silo.OnUrl(HttpMethod.Get, FakeCalendar.Path, _calendar.RespondAsync);
    }

    [Fact]
    public async Task TheSeriesItsSeasonAndItsEpisode_NameTheSeriesNextAiring_Alike()
    {
        _fixture.Series().Season().Episode();
        _calendar.Airs(3, "Chapter 3", "2026-10-30", at: "2026-10-30T20:00:00Z")
            .Premieres(4, "2026-11-20")
            .Airs(1, "Another series", "2026-10-02", season: 1, series: "series-tmdb-7");

        var series = await LoadAsync(SeriesId);
        var season = await LoadAsync(Season3Id);
        var episode = await LoadAsync(EpisodeId);

        Assert.All(new[] { series, season, episode }, page =>
        {
            Assert.Equal("Episode 3 on Oct 30", page.NextAiringText);
            Assert.True(page.HasNextAiring);
        });
        // Today's two windows, the season grid's own, asked once for all three pages.
        Assert.Equal(TodaysWindows, Windows());
    }

    [Fact]
    public async Task ASeasonPremiere_NamesTheSeasonStarting()
    {
        _fixture.Series();
        _calendar.Premieres(4, "2026-10-02");

        var detail = await LoadAsync(SeriesId);

        Assert.Equal("Season 4 starts tomorrow", detail.NextAiringText);
    }

    [Fact]
    public async Task NothingStillToAirInTheNext62Days_ShowsNoPill()
    {
        _fixture.Series();
        // Today's episode without a time may have aired; the other is past today's windows.
        _calendar.Airs(4, "Chapter 4", "2026-10-01").Airs(5, "Chapter 5", "2026-12-10");

        var detail = await LoadAsync(SeriesId);

        Assert.Equal("", detail.NextAiringText);
        Assert.False(detail.HasNextAiring);
    }

    [Fact]
    public async Task AFailedCalendar_ShowsNoPill_AndThePageAsItIs()
    {
        _fixture.Series();
        _calendar.Fails = true;

        var detail = await _fixture.LoadAsync(SeriesId);

        Assert.True(detail.IsReady);
        Assert.False(detail.HasNextAiring);
        Assert.Equal(4, detail.Seasons.Count);
        Assert.Contains(_fixture.Logs.Lines, line => line.StartsWith("Warning ItemDetailViewModel:", StringComparison.Ordinal)
            && line.Contains("503", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePage_DoesNotWaitForTheCalendar()
    {
        _fixture.Series();
        _calendar.Airs(3, "Chapter 3", "2026-10-02");
        _calendar.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detail = _fixture.Create();

        await detail.LoadCommand.ExecuteAsync(SeriesId);

        Assert.True(detail.IsReady);
        Assert.False(detail.HasNextAiring);
        Assert.False(detail.NextAiringRead.IsCompleted);
        _calendar.Gate.SetResult();
        await detail.NextAiringRead;
        Assert.Equal("Episode 3 tomorrow", detail.NextAiringText);
        AssertQuiet();
    }

    [Fact]
    public async Task ThePill_FollowsThePagesNextReading_NotTheClock()
    {
        _fixture.Season();
        _calendar.Airs(3, "Chapter 3", "2026-10-01", at: "2026-10-01T21:00:00Z").Airs(4, "Chapter 4", "2026-10-08");
        var detail = await LoadAsync(Season3Id);
        detail.Activate();
        Assert.Equal("Episode 3 today", detail.NextAiringText);

        // Episode 3 aired at nine; the page shows what it read until it reads again, here once a playback stopped.
        _fixture.Clock.Advance(TimeSpan.FromHours(10));
        Assert.Equal("Episode 3 today", detail.NextAiringText);
        _fixture.Stops.Track(Task.CompletedTask);
        _fixture.Clock.Advance(ItemDetailViewModel.RefreshDelay);
        await detail.Refreshing;
        await detail.NextAiringRead;

        Assert.Equal("Episode 4 on Oct 8", detail.NextAiringText);
        AssertQuiet();
    }

    [Fact]
    public async Task AReadingThatFails_KeepsThePillItShowed()
    {
        _fixture.Season();
        _calendar.Airs(3, "Chapter 3", "2026-10-04");
        var detail = await LoadAsync(Season3Id);
        detail.Activate();

        // The kept answers are gone by the next reading, and the calendar no longer answers.
        _fixture.Clock.Advance(SeriesAirings.KeepFor);
        _calendar.Fails = true;
        _fixture.Stops.Track(Task.CompletedTask);
        _fixture.Clock.Advance(ItemDetailViewModel.RefreshDelay);
        await detail.Refreshing;
        await detail.NextAiringRead;

        Assert.Equal("Episode 3 on Sunday", detail.NextAiringText);
        Assert.Contains(_fixture.Logs.Lines, line => line.StartsWith("Warning ItemDetailViewModel:", StringComparison.Ordinal)
            && line.Contains("503", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMovie_HasNoPill_AndAsksTheCalendarNothing()
    {
        _fixture.Movie();

        var detail = await LoadAsync(MovieId);

        Assert.False(detail.HasNextAiring);
        Assert.Empty(Windows());
    }

    [Fact]
    public async Task AnotherTitleLoadedMeanwhile_GetsNoneOfTheSeriesPill()
    {
        _fixture.Series().Movie();
        _calendar.Airs(3, "Chapter 3", "2026-10-02");
        _calendar.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detail = _fixture.Create();

        await detail.LoadCommand.ExecuteAsync(SeriesId);
        var read = detail.NextAiringRead;
        await detail.LoadCommand.ExecuteAsync(MovieId);
        _calendar.Gate.SetResult();
        await read;

        Assert.Equal("Masterplan", detail.Title);
        Assert.False(detail.HasNextAiring);
    }

    [Fact]
    public async Task LeavingThePage_StopsTheRead()
    {
        _fixture.Series();
        _calendar.Airs(3, "Chapter 3", "2026-10-02");
        _calendar.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detail = _fixture.Create();
        await detail.LoadCommand.ExecuteAsync(SeriesId);
        var read = detail.NextAiringRead;

        detail.Deactivate();
        await read;
        _calendar.Gate.SetResult();

        Assert.False(detail.HasNextAiring);
        AssertQuiet();
    }

    // The page loaded, its places and its next airing included, without a warning on the way.
    private async Task<ItemDetailViewModel> LoadAsync(string id)
    {
        var detail = await _fixture.LoadAsync(id);
        AssertQuiet();
        return detail;
    }

    private void AssertQuiet() =>
        Assert.DoesNotContain(_fixture.Logs.Lines, line => line.StartsWith("Warning ItemDetailViewModel:", StringComparison.Ordinal));

    // The calendar windows asked, by their start.
    private List<(string Start, string End)> Windows() =>
        [.. _fixture.Silo.SentTo(HttpMethod.Get, FakeCalendar.Path).Select(r => (Start: FakeCalendar.Query(r.Uri, "start"), End: FakeCalendar.Query(r.Uri, "end")))
            .OrderBy(w => w.Start, StringComparer.Ordinal)];
}
