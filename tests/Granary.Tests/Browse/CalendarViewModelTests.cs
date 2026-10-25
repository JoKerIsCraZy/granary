using System.Globalization;
using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Profiles;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Browse;

/// <summary>The Calendar, as Silo's web shows it; the clock stands on Thursday, 1 October 2026, in UTC.</summary>
public sealed class CalendarViewModelTests
{
    private const string CalendarPath = "api/v2/calendar";

    /// <summary>A season premiere tonight and a movie on Saturday.</summary>
    private const string Week = """
        {
          "events": [
            {
              "date": "2026-10-01",
              "items": [
                {
                  "content_id": "episode:severance-s02e01", "type": "episode", "title": "Severance", "series_id": "series:severance",
                  "season_number": 2, "episode_number": 1, "episode_title": "Hello, Ms. Cobel", "air_date": "2026-10-01",
                  "local_air_date": "2026-10-01", "air_at": "2026-10-01T21:00:00Z", "badges": ["season_premiere"],
                  "poster_url": "https://s3.example.test/severance.jpg", "watched": false
                }
              ]
            },
            {
              "date": "2026-10-03",
              "items": [
                {
                  "content_id": "movie:dune-3", "type": "movie", "title": "Dune: Part Three", "air_date": "2026-10-03",
                  "local_air_date": "2026-10-03", "air_time": "20:15", "badges": [], "watched": true
                }
              ]
            }
          ]
        }
        """;

    private const string NoEvents = """{ "events": [] }""";

    private readonly LibraryHarness _harness = new();
    private readonly SidebarViewModel _sidebar;

    public CalendarViewModelTests()
    {
        _harness.Answer(CalendarPath, Week);
        _harness.Answer("api/v2/profiles", CollectionPayloads.OneProfile);
        _sidebar = new SidebarViewModel(_harness.Session, new ProfilesApi(_harness.Silo.Http), new CatalogApi(_harness.Silo.Http),
            _harness.Navigation, new ImmediateDispatcher(), _harness.Logs.For<SidebarViewModel>());
    }

    private CalendarViewModel Create() => new(
        _harness.Session, new CalendarApi(_harness.Silo.Http), _sidebar, _harness.Navigation, _harness.Clock, _harness.Logs.For<CalendarViewModel>());

    private async Task<CalendarViewModel> LoadedAsync()
    {
        var page = Create();
        await page.ActivateAsync(isReturn: false);
        return page;
    }

    private string LastQuery => _harness.Queries(CalendarPath)[^1];

    [Fact]
    public async Task TheFirstVisit_ShowsThisWeek_MondayToSunday_FollowedShows()
    {
        var page = await LoadedAsync();

        Assert.Equal("Sep 28 – Oct 4, 2026", page.WeekLabel);
        Assert.StartsWith("start=2026-09-28&end=2026-10-04&filter=following&timezone=", LastQuery, StringComparison.Ordinal);
        Assert.Equal(["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"], page.Days.Select(d => d.Label));
        Assert.Equal(["28", "29", "30", "1", "2", "3", "4"], page.Days.Select(d => d.Day));
        Assert.Equal([new DateOnly(2026, 10, 1)], page.Days.Where(d => d.IsToday).Select(d => d.Date));
        Assert.Equal([new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 3)], page.Days.Where(d => d.HasEvents).Select(d => d.Date));
    }

    [Fact]
    public async Task EachDay_IsARowOfItsAirings_WordedAsTheWeb()
    {
        var page = await LoadedAsync();

        Assert.Equal(["Thursday, October 1st", "Saturday, October 3rd"], page.Rows.Select(r => r.Heading));
        Assert.Equal(["TODAY", ""], page.Rows.Select(r => r.Badge));
        var premiere = page.Rows[0].Cards[0];
        Assert.Equal("Severance", premiere.Title);
        Assert.Equal("S2 · E1 - HELLO, MS. COBEL", premiere.Subtitle);
        Assert.Equal("SEASON PREMIERE", premiere.StatusBadge);
        Assert.Equal(new TimeOnly(21, 0).ToString("t", CultureInfo.CurrentCulture), premiere.Caption);
        Assert.Equal(CalendarViewModel.CardType, premiere.Type);
        var movie = page.Rows[1].Cards[0];
        Assert.Equal("MOVIE", movie.Subtitle);
        Assert.Equal(new TimeOnly(20, 15).ToString("t", CultureInfo.CurrentCulture), movie.Caption);
        Assert.True(movie.IsWatched && movie.ShowsWatchedMark);
    }

    [Fact]
    public async Task AnAiring_OpensItsSeries_AndAMovieItself()
    {
        var page = await LoadedAsync();

        page.OpenCommand.Execute(page.Rows[0].Cards[0]);
        page.OpenCommand.Execute(page.Rows[1].Cards[0]);

        Assert.Equal(["series:severance", "movie:dune-3"], _harness.Navigation.Navigations.Select(n => n.Parameter as string));
    }

    [Fact]
    public async Task TheWeekArrows_AndToday_MoveTheWeek()
    {
        var page = await LoadedAsync();

        await page.NextWeekCommand.ExecuteAsync(null);
        Assert.Equal("Oct 5 – 11, 2026", page.WeekLabel);
        Assert.StartsWith("start=2026-10-05&end=2026-10-11&", LastQuery, StringComparison.Ordinal);
        Assert.DoesNotContain(page.Days, d => d.IsToday);

        await page.ThisWeekCommand.ExecuteAsync(null);
        await page.PreviousWeekCommand.ExecuteAsync(null);
        Assert.StartsWith("start=2026-09-21&end=2026-09-27&", LastQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyWeek_SaysSoForItsPreset_AndOffersTheOthers()
    {
        _harness.Answer(CalendarPath, NoEvents);
        var page = await LoadedAsync();

        page.SelectedPreset = page.Presets.Single(p => p.Label == "Trending");
        await WaitUntilAsync(() => page.State == LibraryLoadState.Empty && LastQuery.Contains("filter=trending", StringComparison.Ordinal));

        Assert.Equal("Nothing trending this week.", page.EmptyText);
        Assert.Equal(["Following", "Show everything"], page.EmptyLinks.Select(l => l.Label));

        page.EmptyLinks[1].Command.Execute(page.EmptyLinks[1].Preset);
        await WaitUntilAsync(() => LastQuery.Contains("filter=everything", StringComparison.Ordinal) && page.State == LibraryLoadState.Empty);
        Assert.Equal("Nothing scheduled this week.", page.EmptyText);
    }

    [Fact]
    public async Task SeveralLibraries_OfferAFilter()
    {
        await _sidebar.RefreshAsync(TestContext.Current.CancellationToken);
        var page = await LoadedAsync();

        Assert.Equal("All Libraries", page.LibraryChoices[0].Label);
        Assert.Equal(_sidebar.Libraries.Select(l => l.Name), page.LibraryChoices.Skip(1).Select(c => c.Label));

        page.SelectedLibrary = page.LibraryChoices[2];
        await WaitUntilAsync(() => LastQuery.EndsWith("&library_id=" + _sidebar.Libraries[1].Id, StringComparison.Ordinal) && page.State == LibraryLoadState.Ready);
    }

    [Fact]
    public async Task ADayPicked_AsksForItsRow_OrSaysNothingIsScheduled()
    {
        var page = await LoadedAsync();
        var requested = new List<DateOnly>();
        page.DayRequested += (_, day) => requested.Add(day);

        page.SelectDayCommand.Execute(page.Days[4]);
        Assert.True(page.HasSelectedDayEmpty);
        Assert.Equal("Nothing scheduled for Friday, October 2nd.", page.SelectedDayEmptyText);
        Assert.True(page.Days[4].IsSelected);

        page.SelectDayCommand.Execute(page.Days[3]);
        Assert.False(page.HasSelectedDayEmpty);
        Assert.Equal([new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 1)], requested);
    }

    [Fact]
    public async Task AWeekThatFails_CanBeTriedAgain()
    {
        _harness.Answer(CalendarPath, ScriptedSilo.Problem(503, "unavailable"), ScriptedSilo.Json(HttpStatusCode.OK, Week));

        var page = await LoadedAsync();
        Assert.Equal(LibraryLoadState.Error, page.State);

        await page.RetryCommand.ExecuteAsync(null);
        Assert.Equal(2, page.Rows.Count);
    }

    [Fact]
    public async Task Back_KeepsTheWeek_WhileTheSidebarReadsItAgain()
    {
        var page = await LoadedAsync();

        await page.ActivateAsync(isReturn: true);
        Assert.Single(_harness.Queries(CalendarPath));

        await page.ActivateAsync(isReturn: false);
        Assert.Equal(2, _harness.Queries(CalendarPath).Count);
    }

    [Fact]
    public async Task ADayPicked_KeepsTheStripsButtons()
    {
        var page = await LoadedAsync();
        var strip = page.Days;

        page.SelectDayCommand.Execute(page.Days[1]);

        Assert.Same(strip, page.Days);
        Assert.Equal([false, true, false, false, false, false, false], page.Days.Select(d => d.IsSelected));
    }

    [Fact]
    public async Task TheSidebar_OpensThisWeekOfEveryLibrary_ButKeepsThePreset()
    {
        await _sidebar.RefreshAsync(TestContext.Current.CancellationToken);
        var page = await LoadedAsync();
        await page.NextWeekCommand.ExecuteAsync(null);
        page.SelectedLibrary = page.LibraryChoices[1];
        page.SelectedPreset = page.Presets.Single(p => p.Label == "Trending");
        await WaitUntilAsync(() => LastQuery.Contains("filter=trending&", StringComparison.Ordinal) && page.State == LibraryLoadState.Ready);
        page.SelectDayCommand.Execute(page.Days[0]);
        int asked = _harness.Queries(CalendarPath).Count;

        await page.ActivateAsync(isReturn: false);

        Assert.Equal(asked + 1, _harness.Queries(CalendarPath).Count);
        Assert.StartsWith("start=2026-09-28&end=2026-10-04&filter=trending&timezone=", LastQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("library_id", LastQuery, StringComparison.Ordinal);
        Assert.Null(page.SelectedDay);
    }

    [Fact]
    public async Task BackWhileTheSidebarReloads_KeepsTheLibrary()
    {
        await _sidebar.RefreshAsync(TestContext.Current.CancellationToken);
        var page = await LoadedAsync();
        page.SelectedLibrary = page.LibraryChoices[1];
        await WaitUntilAsync(() => LastQuery.Contains("library_id=", StringComparison.Ordinal) && page.State == LibraryLoadState.Ready);
        int asked = _harness.Queries(CalendarPath).Count;
        _sidebar.Libraries.Clear();

        await page.ActivateAsync(isReturn: true);

        Assert.Equal(page.LibraryChoices[1], page.SelectedLibrary);
        Assert.Equal(asked, _harness.Queries(CalendarPath).Count);
    }

    [Fact]
    public async Task AnEarlierWeekAnsweringLate_LeavesTheNewerOneShown()
    {
        var release = new TaskCompletionSource();
        _harness.Answer(CalendarPath, LibraryHarness.Held(release, Week), ScriptedSilo.Json(HttpStatusCode.OK, NoEvents));
        var page = Create();
        var first = page.ActivateAsync(isReturn: false);

        page.SelectedPreset = page.Presets.Single(p => p.Label == "Trending");
        await WaitUntilAsync(() => page.State == LibraryLoadState.Empty);
        release.SetResult();
        await first;

        Assert.Equal(LibraryLoadState.Empty, page.State);
        Assert.Empty(page.Rows);
        Assert.Equal("Nothing trending this week.", page.EmptyText);
    }

    [Fact]
    public async Task TheDaysAndTimes_FollowTheViewersZone()
    {
        // Sunday 23:30 UTC is already Monday in Berlin, after the clocks went back.
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var clock = new ZoneClock(new DateTimeOffset(2026, 10, 25, 23, 30, 0, TimeSpan.Zero), berlin);
        _harness.Answer(CalendarPath, """
            { "events": [ { "date": "2026-10-26", "items": [ { "content_id": "movie:m", "type": "movie", "title": "M", "air_date": "2026-10-26",
              "local_air_date": "2026-10-26", "air_at": "2026-10-26T19:00:00Z", "badges": [], "watched": false } ] } ] }
            """);
        var page = new CalendarViewModel(_harness.Session, new CalendarApi(_harness.Silo.Http), _sidebar, _harness.Navigation, clock,
            _harness.Logs.For<CalendarViewModel>());

        await page.ActivateAsync(isReturn: false);

        Assert.StartsWith("start=2026-10-26&end=2026-11-01&filter=following&timezone=Europe%2FBerlin", LastQuery, StringComparison.Ordinal);
        Assert.Equal("TODAY", page.Rows[0].Badge);
        Assert.Equal(new TimeOnly(20, 0).ToString("t", CultureInfo.CurrentCulture), page.Rows[0].Cards[0].Caption);
    }

    [Theory]
    [InlineData(3, "Etc/GMT-3")]
    [InlineData(-5, "Etc/GMT+5")]
    [InlineData(0, "Etc/UTC")]
    public void AZoneWithoutAName_GoesByItsOffset(int hours, string name)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Granary test zone", TimeSpan.FromHours(hours), "Test", "Test");

        Assert.Equal(name, CalendarText.IanaZone(zone, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void AZoneOffByHalfAnHour_IsLeftToTheServer()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Granary test zone", TimeSpan.FromMinutes(330), "Test", "Test");

        Assert.Null(CalendarText.IanaZone(zone, DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData(2026, 10, 1, "Thursday, October 1st")]
    [InlineData(2026, 10, 2, "Friday, October 2nd")]
    [InlineData(2026, 10, 3, "Saturday, October 3rd")]
    [InlineData(2026, 10, 11, "Sunday, October 11th")]
    [InlineData(2026, 10, 12, "Monday, October 12th")]
    [InlineData(2026, 10, 13, "Tuesday, October 13th")]
    [InlineData(2026, 10, 21, "Wednesday, October 21st")]
    [InlineData(2026, 10, 22, "Thursday, October 22nd")]
    [InlineData(2026, 10, 23, "Friday, October 23rd")]
    [InlineData(2026, 10, 31, "Saturday, October 31st")]
    public void DayHeadings_TakeEnglishOrdinals(int year, int month, int day, string heading) =>
        Assert.Equal(heading, CalendarText.DayHeading(new DateOnly(year, month, day)));

    [Fact]
    public void ASunday_BelongsToTheWeekBefore() =>
        Assert.Equal(new DateOnly(2026, 9, 28), CalendarText.WeekStart(new DateOnly(2026, 10, 4)));

    [Fact]
    public void ASeasonWithoutAnEpisode_NamesTheSeason()
    {
        var airing = new CalendarEvent { Type = "season_premiere", SeasonNumber = 3, EpisodeTitle = "Return" };

        Assert.Equal("Season 3 · Return", CalendarText.Subtitle(airing));
        Assert.Equal("", CalendarText.Subtitle(new CalendarEvent { Type = "episode" }));
        Assert.Equal("FINALE", CalendarText.Badge(["finale", "season_premiere"]));
        Assert.Null(CalendarText.AirTime(new CalendarEvent(), TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task TheApi_RefusesAWindowLongerThanTheServerAnswers()
    {
        var api = new CalendarApi(_harness.Silo.Http);
        var start = new DateOnly(2026, 10, 1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            api.GetAsync(ScriptedSilo.Base, start, start.AddDays(31), cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>A clock standing still in a zone of the test's choosing.</summary>
    private sealed class ZoneClock(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => zone;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        Assert.True(condition());
    }
}
