using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Detail;

namespace Granary.Tests.Browse;

/// <summary>The windows and places a season's grid plans for the episodes its listing lacks.</summary>
public sealed class SeasonGapsTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);

    [Fact]
    public void TheWindows_NeverSpanMoreThan31Days_AreAtMostFour_AndAlwaysHoldTodays()
    {
        // Seasons of every shape: random numbers and air dates, some without a date, some ahead of today.
        var random = new Random(20261108);
        for (int season = 0; season < 2000; season++)
        {
            int count = random.Next(1, 30);
            List<Episode> listed = [];
            int number = random.Next(0, 3);
            for (int i = 0; i < count; i++)
            {
                number += random.Next(1, random.Next(2) == 0 ? 3 : 60);
                string? airDate = random.Next(8) == 0 ? null : Today.AddDays(random.Next(-400, 90)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                listed.Add(new Episode { ContentId = $"e{i}", EpisodeNumber = number, AirDate = airDate });
            }
            var gaps = SeasonGaps.Find(listed);

            var windows = SeasonGaps.Windows(gaps, listed.MaxBy(e => e.EpisodeNumber), Today);

            Assert.InRange(windows.Count, 2, SeasonGaps.MaxWindows);
            Assert.All(windows, w => Assert.InRange(w.End.DayNumber - w.Start.DayNumber, 0, CalendarApi.MaxDays));
            Assert.Contains(new SeasonGaps.Window(Today, Today.AddDays(30)), windows);
            Assert.Contains(new SeasonGaps.Window(Today.AddDays(31), Today.AddDays(61)), windows);
        }
    }

    [Fact]
    public void TheDaysAfterTheLastEpisode_EndYesterday_AsTodayIsInTodaysWindow()
    {
        var last = new Episode { ContentId = "e1", EpisodeNumber = 1, AirDate = "2026-09-21" };

        var windows = SeasonGaps.Windows([], last, Today);

        Assert.Equal(new SeasonGaps.Window(new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 30)), windows[0]);
        Assert.Single(windows, w => w.Start <= Today && w.End >= Today);
    }

    [Fact]
    public void ASeasonWithoutEpisodes_AsksOnlyTodaysWindows()
    {
        var windows = SeasonGaps.Windows([], null, Today);

        Assert.Equal([new SeasonGaps.Window(Today, Today.AddDays(30)), new SeasonGaps.Window(Today.AddDays(31), Today.AddDays(61))], windows);
    }

    [Fact]
    public void AMultiEpisodeFile_HoldsAtMostTenEpisodesPastItsFirst()
    {
        var episode = new CatalogItemDetail
        {
            Versions =
            [
                new FileVersion { MultiEpisodeStart = 3, MultiEpisodeEnd = 40 },
                new FileVersion { MultiEpisodeStart = 3, MultiEpisodeEnd = 4 },
                new FileVersion { MultiEpisodeStart = 5, MultiEpisodeEnd = 5 },
                new FileVersion(),
            ],
        };

        Assert.Equal(Enumerable.Range(3, 11), SeasonGaps.Spanned(episode).Order());
    }
}
