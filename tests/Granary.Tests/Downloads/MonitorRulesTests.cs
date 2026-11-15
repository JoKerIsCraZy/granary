using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Downloads;

namespace Granary.Tests.Downloads;

/// <summary>A monitor's rule and status in Silo's Apple app's words (<c>AutoDownloadStatus.swift</c>).</summary>
public sealed class MonitorRulesTests
{
    private const long GB = 1L << 30;

    // A Monday at noon in UTC.
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static DownloadSubscription Monitor(string mode = SubscriptionModes.Future, bool active = true, string? quality = "original",
        int? targetSeason = null, long maxBytes = 0, params int[] seasons) =>
        new() { Id = "sub_1", SeriesId = "sr_1", Mode = mode, Active = active, Quality = quality, TargetSeason = targetSeason, MaxStorageBytes = maxBytes,
            SeasonNumbers = seasons, Etag = "\"m1\"" };

    private static UpcomingEpisode Airing(int season, int episode, DateTimeOffset at, string id = "ep_x") => new(id, "sr_1", season, episode, null, at);

    [Fact]
    public void Summary_SaysWhatTheMonitorTakes_AndAQualityThatIsNotOriginal()
    {
        Assert.Equal("All episodes", MonitorRules.Summary(Monitor(SubscriptionModes.All)));
        Assert.Equal("Future episodes · 10 Mbps", MonitorRules.Summary(Monitor(quality: "10mbps")));
        Assert.Equal("Last season", MonitorRules.Summary(Monitor(SubscriptionModes.LatestSeason)));
        Assert.Equal("Last season · Season 3 on", MonitorRules.Summary(Monitor(SubscriptionModes.LatestSeason, targetSeason: 3)));
        Assert.Equal("Seasons 1, 2 and 4", MonitorRules.Summary(Monitor(SubscriptionModes.SpecificSeasons, seasons: [4, 1, 2])));
        Assert.Equal("Monitored", MonitorRules.Summary(Monitor("someday")));
    }

    [Fact]
    public void SeasonList_NamesOneTwoOrMany()
    {
        Assert.Equal("No seasons", MonitorRules.SeasonList([]));
        Assert.Equal("Season 4", MonitorRules.SeasonList([4]));
        Assert.Equal("Seasons 2 and 3", MonitorRules.SeasonList([3, 2, 3]));
    }

    [Fact]
    public void LimitText_IsWholeGigabytes_ElseTheSize()
    {
        Assert.Equal("10 GB", MonitorRules.LimitText(10 * GB));
        Assert.Equal("1.5 GB", MonitorRules.LimitText(GB + GB / 2));
    }

    [Fact]
    public void Status_APausedMonitorSaysSoFirst_ThenALimit_ThenOneUnderWay_ThenOneWaiting()
    {
        MonitorActivity[] activity =
        [
            new(1, 7, MonitorPhase.Queued, 0),
            new(1, 6, MonitorPhase.Downloading, 0.42),
        ];

        Assert.Equal(new MonitorStatus.Paused(), MonitorRules.StatusOf(Monitor(active: false), activity, [], [], true, Now));
        Assert.Equal(new MonitorStatus.StorageLimit(25 * GB), MonitorRules.StatusOf(Monitor(maxBytes: 25 * GB),
            [.. activity, new(1, 8, MonitorPhase.SeriesLimit, 0)], [], [], true, Now));
        Assert.Equal(new MonitorStatus.Downloading(6, 0.42), MonitorRules.StatusOf(Monitor(), activity, [], [], true, Now));
        Assert.Equal(new MonitorStatus.Waiting(7, MonitorPhase.Queued), MonitorRules.StatusOf(Monitor(), [activity[0]], [], [], true, Now));
    }

    [Fact]
    public void Status_NamesTheLimitThatHoldsTheEpisode_TheSeriesOrAllDownloads()
    {
        // 100 GB for the series and 10 GB for every download on this PC, 9 GB of it taken: a 2 GB episode is held by the PC's.
        var monitor = Monitor(maxBytes: 100 * GB);

        var status = MonitorRules.StatusOf(monitor, MonitorCards.ActivityOf([Held(8, DownloadHold.DownloadsLimit)], "sr_1"), [], [], true, Now,
            downloadsLimit: 10 * GB);

        Assert.Equal("Out of space · 10 GB limit", MonitorRules.StatusLine(status, Now, TimeZoneInfo.Utc));
        // The series' own limit names the monitor's.
        Assert.Equal(new MonitorStatus.StorageLimit(100 * GB), MonitorRules.StatusOf(monitor,
            MonitorCards.ActivityOf([Held(8, DownloadHold.SeriesLimit)], "sr_1"), [], [], true, Now, 10 * GB));
        // Episodes held by each: the first of them names its limit.
        Assert.Equal(new MonitorStatus.StorageLimit(10 * GB), MonitorRules.StatusOf(monitor,
            MonitorCards.ActivityOf([Held(9, DownloadHold.SeriesLimit), Held(7, DownloadHold.DownloadsLimit)], "sr_1"), [], [], true, Now, 10 * GB));
        Assert.Equal(new MonitorStatus.StorageLimit(100 * GB), MonitorRules.StatusOf(monitor,
            MonitorCards.ActivityOf([Held(9, DownloadHold.DownloadsLimit), Held(7, DownloadHold.SeriesLimit)], "sr_1"), [], [], true, Now, 10 * GB));
    }

    // Episode <paramref name="episode"/> of sr_1, queued, 2 GB, held by <paramref name="hold"/>.
    private static DownloadItem Held(int episode, DownloadHold hold) =>
        new("dl_" + episode, "ep_" + episode, "Episode " + episode, "sr_1", "Example Series", 1, episode, DownloadState.Queued, DownloadFailure.None,
            IsPausedByViewer: false, IsNoLongerAvailable: false, FilesMissing: false, WaitsForUnmetered: false, ExpectedBytes: 2 * GB, BytesDone: 0,
            PreparationProgress: null, PosterPath: null, MediaFileId: null, Hold: hold);

    [Fact]
    public void Status_WithNothingUnderWay_NamesTheNextAiringItTakes_ElseUpToDate()
    {
        UpcomingEpisode[] upcoming = [Airing(2, 1, Now.AddDays(10), "ep_21"), Airing(1, 9, Now.AddDays(3), "ep_19"), Airing(1, 8, Now.AddHours(-1), "ep_18")];

        Assert.Equal(new MonitorStatus.Next(upcoming[1]), MonitorRules.StatusOf(Monitor(), [], upcoming, [], true, Now));
        // One this PC has already is not next.
        Assert.Equal(new MonitorStatus.Next(upcoming[0]), MonitorRules.StatusOf(Monitor(), [], upcoming, ["ep_19"], true, Now));
        // Specific seasons take only theirs.
        Assert.Equal(new MonitorStatus.Next(upcoming[0]), MonitorRules.StatusOf(Monitor(SubscriptionModes.SpecificSeasons, seasons: [2]), [], upcoming, [], true, Now));
        Assert.Equal(new MonitorStatus.UpToDate(), MonitorRules.StatusOf(Monitor(), [], [], [], true, Now));
        // Before the airing calendar was read, whether anything is due is not known.
        Assert.Equal(new MonitorStatus.Monitoring(), MonitorRules.StatusOf(Monitor(), [], [], [], false, Now));
    }

    [Fact]
    public void StatusLine_IsTheAppleAppsWords()
    {
        var zone = TimeZoneInfo.Utc;
        Assert.Equal("Paused", MonitorRules.StatusLine(new MonitorStatus.Paused(), Now, zone));
        Assert.Equal("Out of space · 25 GB limit", MonitorRules.StatusLine(new MonitorStatus.StorageLimit(25 * GB), Now, zone));
        Assert.Equal("Downloading Episode 6 · 42%", MonitorRules.StatusLine(new MonitorStatus.Downloading(6, 0.42), Now, zone));
        Assert.Equal("Downloading an episode · 42%", MonitorRules.StatusLine(new MonitorStatus.Downloading(null, 0.42), Now, zone));
        Assert.Equal("Episode 7 is next", MonitorRules.StatusLine(new MonitorStatus.Waiting(7, MonitorPhase.Queued), Now, zone));
        Assert.Equal("Preparing Episode 7", MonitorRules.StatusLine(new MonitorStatus.Waiting(7, MonitorPhase.Preparing), Now, zone));
        Assert.Equal("Episode 7 waits for an unmetered connection", MonitorRules.StatusLine(new MonitorStatus.Waiting(7, MonitorPhase.WaitingForUnmetered), Now, zone));
        Assert.Equal("Episode 6 today", MonitorRules.StatusLine(new MonitorStatus.Next(Airing(1, 6, Now.AddHours(2))), Now, zone));
        Assert.Equal("Episode 6 tomorrow", MonitorRules.StatusLine(new MonitorStatus.Next(Airing(1, 6, Now.AddDays(1))), Now, zone));
        Assert.Equal("Episode 6 on Thursday", MonitorRules.StatusLine(new MonitorStatus.Next(Airing(1, 6, Now.AddDays(3))), Now, zone));
        Assert.Equal("Season 2 starts on Nov 30", MonitorRules.StatusLine(new MonitorStatus.Next(Airing(2, 1, Now.AddDays(14))), Now, zone));
        Assert.Equal("Up to date", MonitorRules.StatusLine(new MonitorStatus.UpToDate(), Now, zone));
        Assert.Equal("Monitoring", MonitorRules.StatusLine(new MonitorStatus.Monitoring(), Now, zone));
    }

    [Fact]
    public void Headline_SaysWhenTheNextOneDownloads()
    {
        var zone = TimeZoneInfo.Utc;
        Assert.Equal("Episode 6 downloads Thursday", MonitorRules.Headline(new MonitorStatus.Next(Airing(1, 6, Now.AddDays(3))), Now, zone));
        Assert.Equal("Season 2 downloads tomorrow", MonitorRules.Headline(new MonitorStatus.Next(Airing(2, 1, Now.AddDays(1))), Now, zone));
        Assert.Equal("Monitoring is paused", MonitorRules.Headline(new MonitorStatus.Paused(), Now, zone));
    }

    [Fact]
    public void TheDialogsRows_AreTheAppleAppsWords_InSentenceCase()
    {
        Assert.Equal(["All episodes", "Future episodes", "Last season", "Custom"],
            new[] { SubscriptionModes.All, SubscriptionModes.Future, SubscriptionModes.LatestSeason, SubscriptionModes.SpecificSeasons }.Select(MonitorRules.ModeName));
        Assert.Equal("Season 3 and anything newer", MonitorRules.ModeDetail(SubscriptionModes.LatestSeason, 3, []));
        Assert.Equal("The latest season and anything newer", MonitorRules.ModeDetail(SubscriptionModes.LatestSeason, null, []));
    }

    [Fact]
    public void InGerman_TheWordsAreTheAppleApps_AndTheDayAndSizesAreWrittenAsGermanWritesThem()
    {
        using var german = AppText.Use(TextLanguage.Load("de"));
        var zone = TimeZoneInfo.Utc;

        Assert.Equal("Künftige Folgen", MonitorRules.ModeName(SubscriptionModes.Future));
        Assert.Equal("Staffel 3 und alles Neuere", MonitorRules.ModeDetail(SubscriptionModes.LatestSeason, 3, []));
        Assert.Equal("Letzte Staffel · ab Staffel 3", MonitorRules.Summary(Monitor(SubscriptionModes.LatestSeason, targetSeason: 3)));
        Assert.Equal("Staffeln 1, 2 und 4", MonitorRules.SeasonList([4, 1, 2]));
        Assert.Equal("1,5 GB", MonitorRules.LimitText(GB + GB / 2));
        Assert.Equal("Kein Speicherplatz · Limit 25 GB", MonitorRules.StatusLine(new MonitorStatus.StorageLimit(25 * GB), Now, zone));
        Assert.Equal("Folge 7 wartet auf eine nicht getaktete Verbindung",
            MonitorRules.StatusLine(new MonitorStatus.Waiting(7, MonitorPhase.WaitingForUnmetered), Now, zone));
        Assert.Equal("Folge 6 am Donnerstag", MonitorRules.StatusLine(new MonitorStatus.Next(Airing(1, 6, Now.AddDays(3))), Now, zone));
        Assert.Equal("Staffel 2 startet am 30. Nov.", MonitorRules.StatusLine(new MonitorStatus.Next(Airing(2, 1, Now.AddDays(14))), Now, zone));
        Assert.Equal("Folge 6 wird am Donnerstag heruntergeladen", MonitorRules.Headline(new MonitorStatus.Next(Airing(1, 6, Now.AddDays(3))), Now, zone));
    }
}
