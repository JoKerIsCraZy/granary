using Granary.Core.Api;
using Granary.Core.Api.Playback;
using Granary.Core.Api.Subtitles;
using Granary.Core.Playback;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Player;

namespace Granary.Tests.ViewModels;

/// <summary>
/// The player's words come from the catalogs: the web player's where it has them, in the language on screen, and
/// Granary's own for what only Granary shows.
/// </summary>
public sealed class PlayerTextTests
{
    private static readonly WatchDetail Episode = new()
    {
        Type = WatchItemTypes.Episode, Title = "Schulanfang", SeriesTitle = "ted", SeasonNumber = 1, EpisodeNumber = 5,
    };

    private static SubtitleSyncState Synced(long offsetMs)
    {
        var timing = new SubtitleTiming { OffsetMs = offsetMs, RawScale = 1 };
        return new()
        {
            Key = "stored-12",
            Source = "downloaded",
            Timing = timing,
            Sync = new() { Id = "j1", Status = SubtitleSyncStatuses.Synced, Trigger = "manual", Result = timing },
        };
    }

    [Fact]
    public void TheLineUnderTheTitle_IsTheWebPlayers_InTheLanguageOnScreen()
    {
        Assert.Equal(("ted", "S1 · E5 — Schulanfang"), PlaybackStarter.Titles(Episode));

        using var german = AppText.Use(TextLanguage.Load("de"));
        Assert.Equal(("ted", "S1 · F5 — Schulanfang"), PlaybackStarter.Titles(Episode));
        Assert.Equal(("Schulanfang", "S1 · F5"), PlaybackStarter.Titles(Episode with { SeriesTitle = null }));
    }

    [Fact]
    public void ASubtitlesTiming_IsWordedInTheLanguageOnScreen()
    {
        Assert.Equal("Synced −3.0 s", SubtitleSyncText.Status(Synced(-3000)));
        Assert.Equal("Sync failed. Try again.", SubtitleSyncText.Failure("something_new"));

        using var german = AppText.Use(TextLanguage.Load("de"));
        Assert.Equal("Synchronisiert: −3.0 Sek.", SubtitleSyncText.Status(Synced(-3000)));
    }

    [Fact]
    public void TheMenusRows_AreWordedInTheLanguageOnScreen()
    {
        using var german = AppText.Use(TextLanguage.Load("de"));

        Assert.Equal("Aus", SubtitleTrackOption.For([], selected: null)[0].Title);
        Assert.Equal(("Extern", "Heruntergeladen", "Eingebettet"),
            (TrackText.SubtitleSource(SubtitleSources.External), TrackText.SubtitleSource(SubtitleSources.Downloaded), TrackText.SubtitleSource(null)));
        Assert.Equal(("Unbekannt", "Unbekannte Sprache (xx)"), (TrackText.SubtitleLanguage(" "), TrackText.LanguageName("xx")));
        Assert.Equal("Automatisch", QualityOption.For([new AvailableQuality { Label = "720p", BitrateKbps = 4500 }], "auto", null, isLimited: false)[0].Label);
        Assert.Equal("Auto · nach Puffern begrenzt", QualityOption.For([new AvailableQuality { Label = "720p" }], "auto", null, isLimited: true)[0].Label);
        Assert.Equal(("Transkodierung", "Direktwiedergabe"), (StreamMethods.Label(StreamMethod.Transcode), StreamMethods.Label(StreamMethod.DirectPlay)));
        Assert.Equal(("Rückblick", "Abspann / Outro"), (MarkerEditorViewModel.Label(WatchMarkerKinds.Recap), MarkerEditorViewModel.Label(WatchMarkerKinds.Credits)));
    }

    [Theory]
    [InlineData(4500, "4.5 Mbps")]
    [InlineData(6000, "6 Mbps")]
    [InlineData(300, "300 kbps")]
    [InlineData(0, "")]
    public void ARungsBitrate_KeepsItsDigits_InTheWebsMessage(int kbps, string text) => Assert.Equal(text, QualityOption.FormatBitrate(kbps));

    [Fact]
    public void ThePlaybackInfo_HeadsItsSectionsInTheLanguageOnScreen_AndNamesMpvsDecoding()
    {
        var english = PlaybackInfo.Sections(null, new PlaybackStats(1920, 1080, 0, 0, "d3d11va"), 2560, 1440);
        Assert.Equal(["Player", "Video Info"], english.Select(section => section.Title));
        Assert.Contains(new PlaybackInfoRow("Decoding", "Hardware (d3d11va)"), english[1].Rows);

        using var german = AppText.Use(TextLanguage.Load("de"));
        Assert.Equal(["Player", "Videoinfos"], PlaybackInfo.Sections(null, PlaybackStats.None, 0, 0).Select(section => section.Title));
    }

    [Fact]
    public void AChapterWithoutATitle_IsNamedByItsPlace()
    {
        var rows = ChapterOption.For([new PlaybackChapter("Opening", 0, 90, null), new PlaybackChapter(" ", 90, 180, null)], current: 0);

        Assert.Equal(["Opening", "Chapter 2"], rows.Select(row => row.Title));
    }

    [Fact]
    public void AStartThatFails_IsWordedForTheViewer()
    {
        Assert.Equal(("This title is no longer available.", false), Said(Refusal(404)));
        Assert.Equal(("The server could not start playback (error 500).", true), Said(Refusal(500)));
        Assert.Equal(("Too many requests. Please wait a moment and try again.", true), Said(Refusal(429)));

        static SiloApiException Refusal(int status) => new(status, new ApiProblem(ApiProblem.TypeOrigin + "refused", "refused", status, null, null, null));

        static (string, bool) Said(Exception ex)
        {
            var failure = PlaybackStarter.Describe(ex);
            return (failure.Message, failure.CanRetry);
        }
    }
}
