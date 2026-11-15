using System.Net;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Core.ViewModels.Downloads;
using Granary.Tests.Browse;
using Granary.Tests.Profiles;
using static Granary.Tests.Downloads.DownloadHarness;

namespace Granary.Tests.Downloads;

/// <summary>
/// The airings a monitor can name as next, read from the calendar as Silo's Apple app reads them (<c>AutoDownloadSchedule</c>),
/// with each monitored series' title, for the profile and account the read began for.
/// </summary>
public sealed class MonitorScheduleTests : IAsyncDisposable
{
    private const string CalendarPath = "api/v2/calendar";

    // Episode 6 of Example Series airs in four days.
    private const string Calendar = """
        { "events": [ { "date": "2026-10-05", "items": [
            { "content_id": "ep_6", "type": "episode", "title": "Example Series", "series_id": "sr_1", "season_number": 1,
              "episode_number": 6, "air_date": "2026-10-05" } ] } ] }
        """;

    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly DownloadHarness _h = new();
    private SeriesMonitors? _monitors;

    public MonitorScheduleTests()
    {
        _h.Silo.On(HttpMethod.Get, CapabilityPath, ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Capability()))
            .On(HttpMethod.Get, "api/v2/downloads/subscriptions", ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Page(null, MonitorPayloads.Monitor())))
            .On(HttpMethod.Post, "api/v2/downloads/subscriptions/sync", ScriptedSilo.Json(HttpStatusCode.OK, MonitorPayloads.Sync()))
            .On(HttpMethod.Get, CalendarPath, ScriptedSilo.Json(HttpStatusCode.OK, Calendar))
            .On(HttpMethod.Get, ItemDetailFixture.ItemPath("sr_1"), ItemDetailFixture.Ok(TitlePayloads.Series));
    }

    public async ValueTask DisposeAsync()
    {
        _monitors?.Dispose();
        await _h.DisposeAsync();
    }

    // Profile 7's monitor of sr_1, read, and its schedule.
    private async Task<MonitorSchedule> ScheduleAsync()
    {
        _monitors = new SeriesMonitors(new DownloadSubscriptionsApi(_h.Silo.Http), _h.Api, _h.Store, new MonitorBook(), _h.NewManager(), _h.Session,
            _h.Time, _h.Logs.For<SeriesMonitors>());
        await _monitors.SyncAsync(Token);
        return new MonitorSchedule(new CalendarApi(_h.Silo.Http), new CatalogApi(_h.Silo.Http), _h.Session, _monitors, _h.Time,
            _h.Logs.For<MonitorSchedule>());
    }

    private IReadOnlyList<SentRequest> Reads =>
        [.. _h.Silo.SentTo(HttpMethod.Get, CalendarPath), .. _h.Silo.SentTo(HttpMethod.Get, ItemDetailFixture.ItemPath("sr_1"))];

    [Fact]
    public async Task ItsReads_GoOnlyAsTheProfileAndAccountTheyAreFor()
    {
        var schedule = await ScheduleAsync();

        await schedule.RefreshAsync(force: false, Token);

        Assert.Equal(2, Reads.Count);
        Assert.All(Reads, read => Assert.Equal(("1", "7"), (read.AsAccount, read.AsProfile)));
        Assert.True(schedule.IsKnown);
        Assert.Equal("Example Series", schedule.Info("sr_1")?.Title);
    }

    [Fact]
    public async Task AReadOverTakenByAnotherProfile_IsNotSent_AndGoesWithTheNextRefresh()
    {
        var schedule = await ScheduleAsync();
        // The server acts as profile 8 by the time the reads go out: they are refused unsent, as is the rest of that refresh.
        _h.Silo.ActingProfile = () => "8";

        await schedule.RefreshAsync(force: false, Token);

        Assert.Empty(Reads);
        Assert.False(schedule.IsKnown);
        Assert.Null(schedule.Info("sr_1"));

        _h.Silo.ActingProfile = null;
        await schedule.RefreshAsync(force: false, Token);

        Assert.True(schedule.IsKnown);
        Assert.Equal("Example Series", schedule.Info("sr_1")?.Title);
    }

    [Fact]
    public async Task AReadOverTakenByAnotherAccount_IsNotSent()
    {
        var schedule = await ScheduleAsync();
        // Another account signed in meanwhile, and its profile has the same id.
        _h.Silo.ActingAccount = () => "2";

        await schedule.RefreshAsync(force: false, Token);

        Assert.Empty(Reads);
        Assert.False(schedule.IsKnown);
        Assert.Null(schedule.Info("sr_1"));
    }

    private static CalendarEvent Airing(string id, string type = "episode", int? episode = 6, DateTimeOffset? airAt = null, string? day = null) => new()
    {
        ContentId = id,
        Type = type,
        SeriesId = "sr_1",
        SeasonNumber = 1,
        EpisodeNumber = episode,
        AirAt = airAt,
        LocalAirDate = day,
    };

    [Fact]
    public void AnAiringCountsUntilItsTime_OrFromTheNextDay_WhenOnlyItsDayIsKnown()
    {
        var week = new CalendarWeek
        {
            Events =
            [
                new CalendarDay
                {
                    Date = "2026-11-16",
                    Items =
                    [
                        Airing("ep_aired", airAt: Now.AddHours(-1)),
                        Airing("ep_tonight", airAt: Now.AddHours(8)),
                        Airing("ep_today", day: "2026-11-16"),
                        Airing("ep_tomorrow", day: "2026-11-17"),
                        Airing("se_2", type: "season_premiere", episode: null, day: "2026-11-20"),
                        Airing("mv_1", type: "movie", day: "2026-11-18"),
                    ],
                },
            ],
        };

        var upcoming = MonitorSchedule.UpcomingOf(week, Now, TimeZoneInfo.Utc);

        var list = upcoming["sr_1"];
        Assert.Equal(["ep_tonight", "ep_tomorrow", "se_2"], list.Select(e => e.ContentId));
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero), list[1].AirDate);
        Assert.Equal(1, list[2].EpisodeNumber);
    }
}
