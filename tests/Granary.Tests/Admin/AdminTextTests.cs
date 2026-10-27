using Granary.Core.Api.Admin;
using Granary.Core.ViewModels.Admin;

namespace Granary.Tests.Admin;

/// <summary>The admin pages' words for numbers and sessions, as the web's <c>format.ts</c> tests have them.</summary>
public sealed class AdminTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "0 Mbps")]
    [InlineData(0.04, "0.0 Mbps")]
    [InlineData(2.46, "2.5 Mbps")]
    [InlineData(5, "5.0 Mbps")]
    [InlineData(9.96, "10.0 Mbps")]
    [InlineData(10, "10 Mbps")]
    [InlineData(1234.5, "1,235 Mbps")]
    [InlineData(double.NaN, "—")]
    public void Mbps_KeepsOnePlaceUnderTen(double mbps, string expected) => Assert.Equal(expected, AdminText.Mbps(mbps));

    [Theory]
    [InlineData(0, "0")]
    [InlineData(0.5, "0.5")]
    [InlineData(200, "200")]
    public void MbpsValue_IsTheNumberAlone(double mbps, string expected) => Assert.Equal(expected, AdminText.MbpsValue(mbps));

    [Theory]
    [InlineData(-1, "—")]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1000, "1.0 KB")]
    [InlineData(999_960, "1.0 MB")]
    [InlineData(48_300_000_000, "48.3 GB")]
    [InlineData(99_960_000_000, "100 GB")]
    [InlineData(123_400_000_000, "123 GB")]
    [InlineData(1_200_000_000_000, "1.2 TB")]
    public void DataTotal_PicksTheUnitOnTheRoundedValue(long bytes, string expected) => Assert.Equal(expected, AdminText.DataTotal(bytes));

    [Theory]
    [InlineData(0, "0 GB")]
    [InlineData(549_755_813_888, "512 GB")]
    [InlineData(1_099_511_627_776, "1.0 TB")]
    [InlineData(111_929_022_480_384, "101.8 TB")]
    public void Storage_CountsInBinaryUnits(long bytes, string expected) => Assert.Equal(expected, AdminText.Storage(bytes));

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(20, "1m")]
    [InlineData(150, "3m")]
    [InlineData(3599, "60m")]
    [InlineData(3600, "1h")]
    [InlineData(11_160, "3.1h")]
    public void WatchTime_StaysInMinutesUnderAnHour(long seconds, string expected) => Assert.Equal(expected, AdminText.WatchTime(seconds));

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(720, "12m")]
    [InlineData(723, "12m 3s")]
    [InlineData(10_800, "3h")]
    [InlineData(11_100, "3h 5m")]
    [InlineData(86_400, "1d")]
    [InlineData(273_600, "3d 4h")]
    public void Uptime_UsesTheCoarsestUsefulUnit(long seconds, string expected) =>
        Assert.Equal(expected, AdminText.Uptime(Now.AddSeconds(-seconds), Now));

    [Fact]
    public void Uptime_OfAClockAhead_IsZero() => Assert.Equal("0s", AdminText.Uptime(Now.AddMinutes(5), Now));

    [Theory]
    [InlineData(0.0612, "0.06 ms")]
    [InlineData(9.999, "10 ms")]
    [InlineData(12.4, "12 ms")]
    public void Latency_KeepsTwoPlacesUnderTenMilliseconds(double ms, string expected) => Assert.Equal(expected, AdminText.Latency(ms));

    [Theory]
    [InlineData(30, "Just now")]
    [InlineData(5 * 60 + 59, "5m ago")]
    [InlineData(2 * 3600 + 3000, "2h ago")]
    [InlineData(3 * 86_400 + 80_000, "3d ago")]
    public void Ago_RoundsEachUnitDown(long seconds, string expected) => Assert.Equal(expected, AdminText.Ago(Now.AddSeconds(-seconds), Now));

    [Theory]
    [InlineData(59, "Updated less than 1 minute ago")]
    [InlineData(60, "Updated 1 minute ago")]
    [InlineData(300, "Updated 5 minutes ago")]
    public void Updated_CountsWholeMinutes(long seconds, string expected) => Assert.Equal(expected, AdminText.Updated(Now.AddSeconds(-seconds), Now));

    [Fact]
    public void Counts_AreGroupedAndSingularForOne()
    {
        Assert.Equal("12,480", AdminText.Count(12_480));
        Assert.Equal("1 file", AdminText.Files(1));
        Assert.Equal("2,048 files", AdminText.Files(2048));
        Assert.Equal("1 play", AdminText.Plays(1));
        Assert.Equal("3 plays", AdminText.Plays(3));
        Assert.Equal("1 session", AdminText.Sessions(1));
        Assert.Equal("0 sessions", AdminText.Sessions(0));
    }

    [Fact]
    public void Episode_IsNamedByItsEpisode_UnderItsSeries()
    {
        var session = new AdminPlaybackSession
        {
            MediaTitle = "Secrets", MediaType = "episode", SeriesName = "Dark", SeasonNumber = 1, EpisodeNumber = 3, EpisodeName = "Past and Present",
        };

        Assert.Equal("Past and Present", AdminText.Title(session));
        Assert.Equal("S1 · E3 — Dark", AdminText.Subtitle(session));
        Assert.Equal("S1E3", AdminText.Title(session with { EpisodeName = null }));
    }

    [Fact]
    public void Movie_IsNamedByItsTitle_OrItsFile()
    {
        var session = new AdminPlaybackSession { MediaTitle = "Dune", MediaType = "movie", MediaFileId = "42" };

        Assert.Equal("Dune", AdminText.Title(session));
        Assert.Equal("Movie", AdminText.Subtitle(session));
        Assert.Equal("File #42", AdminText.Title(session with { MediaTitle = "" }));
        Assert.Equal("", AdminText.Subtitle(session with { MediaType = "" }));
        // Without its season and episode numbers an episode reads as its media.
        Assert.Equal("Series", AdminText.Subtitle(session with { MediaType = "episode", SeriesName = "Dark" }));
    }

    [Fact]
    public void Viewer_FallsBackToIds()
    {
        var session = new AdminPlaybackSession { UserId = "9", ProfileId = "p1" };

        Assert.Equal("User #9", AdminText.Username(session));
        Assert.Equal("p1", AdminText.Profile(session));
        Assert.Equal("Kids", AdminText.Profile(session with { ProfileName = "Kids" }));
        Assert.Equal("elena", AdminText.Username(session with { Username = "elena" }));
    }

    [Fact]
    public void Client_PrefersItsLabel_ThenNameAndVersion()
    {
        var session = new AdminPlaybackSession { ClientName = "Granary", ClientVersion = "0.3.0" };

        Assert.Equal("Granary 0.3.0", AdminText.Client(session));
        Assert.Equal("Granary", AdminText.Client(session with { ClientVersion = " " }));
        Assert.Equal("Silo for Android", AdminText.Client(session with { ClientLabel = " Silo for Android " }));
        Assert.Equal("", AdminText.Client(new AdminPlaybackSession()));
    }

    [Fact]
    public void Initial_IsTheFirstLetterUpperCased()
    {
        Assert.Equal("E", AdminText.Initial("elena"));
        Assert.Equal("", AdminText.Initial(""));
    }
}
