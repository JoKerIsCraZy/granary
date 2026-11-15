using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Downloads;

namespace Granary.Tests.Downloads;

/// <summary>The airings a monitor can name as next, read from the calendar as Silo's Apple app reads them (<c>AutoDownloadSchedule</c>).</summary>
public sealed class MonitorScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

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
