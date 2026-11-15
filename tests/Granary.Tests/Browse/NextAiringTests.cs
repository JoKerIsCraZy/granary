using System.Globalization;
using Granary.Core.ViewModels.Detail;
using Granary.Tests.Fakes;

namespace Granary.Tests.Browse;

/// <summary>
/// Which airing a series' pages name as its next, and how, as Apple's auto-download status does
/// (<c>AutoDownloadSchedule.upcoming</c>, <c>AutoDownloadRules.nextEpisode</c> and <c>.day</c>). The clock stands on
/// Thursday, 1 October 2026, at noon in UTC.
/// </summary>
public sealed class NextAiringTests
{
    private readonly ManualTimeProvider _clock = new();

    private CalendarNow Now => CalendarNow.Of(_clock);

    [Fact]
    public void AnAiringWithItsTime_IsNext_UntilItAirs()
    {
        EpisodeAiring[] airings = [At(3, 5, "2026-10-01T09:00:00Z"), At(3, 6, "2026-10-01T21:00:00Z")];

        var next = NextAiring.Find(airings, Now);

        Assert.Equal((3, 6), Numbers(next));
        _clock.Advance(TimeSpan.FromHours(9));
        Assert.Null(NextAiring.Find(airings, Now));
    }

    [Fact]
    public void AnAiringWithOnlyItsDate_CountsFromTheNextDay_AsWhetherTodaysAiredIsUnknown()
    {
        Assert.Null(NextAiring.Find([On(3, 5, "2026-10-01")], Now));

        var next = NextAiring.Find([On(3, 5, "2026-10-01"), On(3, 6, "2026-10-02")], Now);

        Assert.Equal((3, 6), Numbers(next));
    }

    [Fact]
    public void TheFirstToAir_IsNext_InWhicheverSeason()
    {
        var next = NextAiring.Find([At(3, 9, "2026-10-05T20:00:00Z"), On(3, 10, "2026-10-12"), At(4, 1, "2026-10-03T20:00:00Z")], Now);

        Assert.Equal((4, 1), Numbers(next));
    }

    [Fact]
    public void OnOneDay_ADateWithoutATimeGoesFirst_AndOneMomentGoesToTheLowerNumber()
    {
        // Apple reads a date without a time as the start of its day.
        Assert.Equal((3, 9), Numbers(NextAiring.Find([At(3, 8, "2026-10-04T20:00:00Z"), On(3, 9, "2026-10-04")], Now)));
        Assert.Equal((3, 6), Numbers(NextAiring.Find([At(3, 7, "2026-10-04T20:00:00Z"), At(3, 6, "2026-10-04T20:00:00Z")], Now)));
        Assert.Equal((3, 7), Numbers(NextAiring.Find([At(4, 1, "2026-10-04T20:00:00Z"), At(3, 7, "2026-10-04T20:00:00Z")], Now)));
    }

    [Fact]
    public void Specials_AreNeverNext()
    {
        var next = NextAiring.Find([On(0, 1, "2026-10-02"), On(3, 2, "2026-10-04")], Now);

        Assert.Equal((3, 2), Numbers(next));
    }

    [Fact]
    public void NothingStillToAir_IsNoNext()
    {
        Assert.Null(NextAiring.Find([], Now));
        Assert.Null(NextAiring.Find([At(3, 5, "2026-09-30T20:00:00Z"), On(3, 6, "2026-09-30")], Now));
    }

    [Fact]
    public void ASeasonPremiere_IsTheSeasonStarting()
    {
        var premiere = new EpisodeAiring(4, 1, null, "2026-10-15", "2026-10-15", null, IsSeasonPremiere: true);

        var next = NextAiring.Find([premiere, On(3, 9, "2026-10-20")], Now);

        Assert.Same(premiere, next);
        Assert.Equal("Season 4 starts on Oct 15", NextAiring.Text(premiere, Now));
    }

    [Theory]
    [InlineData("2026-10-01T20:00:00Z", "Episode 6 today")]
    [InlineData("2026-10-02T20:00:00Z", "Episode 6 tomorrow")]
    [InlineData("2026-10-03T20:00:00Z", "Episode 6 on Saturday")]
    [InlineData("2026-10-07T20:00:00Z", "Episode 6 on Wednesday")]
    [InlineData("2026-10-08T20:00:00Z", "Episode 6 on Oct 8")]
    [InlineData("2026-11-19T20:00:00Z", "Episode 6 on Nov 19")]
    public void AnEpisode_IsNamedByItsNumber_AndItsDay(string instant, string text) =>
        Assert.Equal(text, NextAiring.Text(At(3, 6, instant), Now));

    [Theory]
    [InlineData("2026-10-01T20:00:00Z", "Season 4 starts today")]
    [InlineData("2026-10-02T20:00:00Z", "Season 4 starts tomorrow")]
    [InlineData("2026-10-06T20:00:00Z", "Season 4 starts on Tuesday")]
    [InlineData("2026-12-01T20:00:00Z", "Season 4 starts on Dec 1")]
    public void ASeasonsFirstEpisode_IsTheSeasonStarting(string instant, string text) =>
        Assert.Equal(text, NextAiring.Text(At(4, 1, instant), Now));

    [Fact]
    public void ADateWithoutATime_IsNamedByThatDay()
    {
        Assert.Equal("Episode 6 tomorrow", NextAiring.Text(On(3, 6, "2026-10-02"), Now));
        Assert.Equal("Episode 6 on Sunday", NextAiring.Text(On(3, 6, "2026-10-04"), Now));
    }

    [Fact]
    public void TheDay_IsTheViewers()
    {
        // Two hours ahead of UTC: 11 pm in UTC is already tomorrow here, and 9 pm is still today.
        _clock.Zone = TimeZoneInfo.CreateCustomTimeZone("Granary two hours ahead", TimeSpan.FromHours(2), "Two ahead", "Two ahead");

        Assert.Equal("Episode 6 tomorrow", NextAiring.Text(At(3, 6, "2026-10-01T23:00:00Z"), Now));
        Assert.Equal("Episode 6 today", NextAiring.Text(At(3, 6, "2026-10-01T21:00:00Z"), Now));
    }

    [Fact]
    public void InAZoneTheServerHasNoNameFor_AnInstantIsStillOnTheViewersDay_AndADateOnTheCalendars()
    {
        // Half an hour off the whole hours, with no IANA name: the server reckons its days in UTC. At 8 pm in UTC it is
        // already half past one on 2 October here, while the calendar's day is still 1 October.
        _clock.Zone = TimeZoneInfo.CreateCustomTimeZone("Granary half hour", TimeSpan.FromMinutes(330), "Half hour", "Half hour");
        _clock.Advance(TimeSpan.FromHours(8));

        // 8 pm on 2 October here.
        Assert.Equal("Episode 6 today", NextAiring.Text(At(3, 6, "2026-10-02T14:30:00Z"), Now));
        // Listed under 2 October in UTC.
        Assert.Equal("Episode 7 tomorrow", NextAiring.Text(On(3, 7, "2026-10-02"), Now));
        Assert.Equal((3, 7), Numbers(NextAiring.Find([On(3, 7, "2026-10-02")], Now)));
    }

    [Fact]
    public void TheNumber_IsNeverGrouped()
    {
        Assert.Equal("Episode 1024 on Oct 8", NextAiring.Text(At(3, 1024, "2026-10-08T20:00:00Z"), Now));
    }

    private static EpisodeAiring At(int season, int episode, string instant) =>
        new(season, episode, null, instant[..10], instant[..10], DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture));

    private static EpisodeAiring On(int season, int episode, string day) => new(season, episode, null, day, day, null);

    private static (int, int)? Numbers(EpisodeAiring? airing) => airing is null ? null : (airing.SeasonNumber, airing.EpisodeNumber);
}
