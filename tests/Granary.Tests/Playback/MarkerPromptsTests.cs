using Granary.Core.Api.Playback;
using Granary.Core.Playback;

namespace Granary.Tests.Playback;

/// <summary>The skip pills, as Silo's intro skip design (docs/api-contract.md, 11.2) and its web player have them.</summary>
public sealed class MarkerPromptsTests
{
    private static readonly PlaybackMarker Intro = new(WatchMarkerKinds.Intro, 60, 120);
    private static readonly PlaybackMarker Credits = new(WatchMarkerKinds.Credits, 2500, 2600);
    private static readonly PlaybackMarker Recap = new(WatchMarkerKinds.Recap, 0, 40);

    private readonly MarkerPrompts _prompts = new();
    private TimeSpan _now;

    private MarkerPrompts With(MarkerSettings? settings = null)
    {
        _prompts.Reset([Recap, Intro, Credits], settings ?? MarkerSettings.Default);
        return _prompts;
    }

    private double? At(double position, MarkerActivity activity = MarkerActivity.Playing, double afterSeconds = 0.25)
    {
        _now += TimeSpan.FromSeconds(afterSeconds);
        return _prompts.Update(position, activity, _now);
    }

    [Fact]
    public void Ask_OffersToSkipTheIntro_AndSkippingSeeksToItsEnd()
    {
        With();

        Assert.Null(At(61));
        Assert.Equal("Skip Intro", _prompts.Prompt?.Label);

        Assert.Equal(120, _prompts.Select());
        Assert.Null(At(120));
        Assert.Null(_prompts.Prompt);
    }

    [Fact]
    public void Ask_GoesAwayAfterFiveSecondsOfPlaying_AndComesBackWhenTheViewerSeeksIn()
    {
        With();
        At(61);
        At(63, afterSeconds: 2);
        Assert.Equal(0.4, _prompts.Prompt!.Progress!.Value, 2);

        At(66, afterSeconds: 3);
        Assert.Null(_prompts.Prompt);
        At(70);
        Assert.Null(_prompts.Prompt);

        At(130);
        At(65);
        Assert.Equal("Skip Intro", _prompts.Prompt?.Label);
    }

    [Fact]
    public void APause_StopsThePromptsClock_ThoughThePlayerSaysNothingWhilePaused()
    {
        // The player reports the pause and the resume, nothing in between.
        With();
        At(61);
        At(61.5, MarkerActivity.Paused, afterSeconds: 0.5);

        At(61.5, afterSeconds: 30);
        Assert.Equal("Skip Intro", _prompts.Prompt?.Label);
        Assert.Equal(0.1, _prompts.Prompt!.Progress!.Value, 2);

        At(62, afterSeconds: 1);
        Assert.Equal(0.3, _prompts.Prompt!.Progress!.Value, 2);
    }

    [Fact]
    public void AShortStall_CountsAsPlaying_ALongOneOnlyForItsFirstSeconds()
    {
        With();
        At(61);

        At(61, MarkerActivity.Stalled, afterSeconds: 0);
        At(62, afterSeconds: 1);
        Assert.Equal(0.2, _prompts.Prompt!.Progress!.Value, 2);

        At(62, MarkerActivity.Stalled, afterSeconds: 0);
        At(62, afterSeconds: 20);
        Assert.Equal("Skip Intro", _prompts.Prompt?.Label);
        Assert.Equal(0.5, _prompts.Prompt!.Progress!.Value, 2);
    }

    [Fact]
    public void TheClock_StartsOnlyOncePlaybackRuns()
    {
        With();

        At(61, MarkerActivity.Stalled);
        At(61, afterSeconds: 3);
        Assert.Equal(0, _prompts.Prompt!.Progress!.Value, 2);

        At(62, afterSeconds: 1);
        Assert.Equal(0.2, _prompts.Prompt!.Progress!.Value, 2);
    }

    [Fact]
    public void ASuspendedPlayback_StopsTheClock_UntilItGoesOn()
    {
        With();
        At(61);
        _now += TimeSpan.FromSeconds(1);
        _prompts.Suspend(_now);

        At(62, afterSeconds: 60);

        Assert.Equal(0.2, _prompts.Prompt!.Progress!.Value, 2);
    }

    [Fact]
    public void AnotherReadOfTheMarkers_KeepsTheShowingPrompt_AndWhatWasResolved()
    {
        With();
        At(61);
        At(63, afterSeconds: 2);

        _prompts.Reset([Recap, Intro with { EndSeconds = 121 }, Credits], MarkerSettings.Default);
        Assert.Equal(0.4, _prompts.Prompt!.Progress!.Value, 2);
        Assert.Equal(121, _prompts.Select());

        _prompts.Reset([Recap, Intro with { StartSeconds = 59 }, Credits], MarkerSettings.Default);
        At(65);
        Assert.Null(_prompts.Prompt);
    }

    [Fact]
    public void Dismissing_HidesTheIntroPrompt_ForGood()
    {
        With();
        At(61);

        Assert.True(_prompts.Dismiss());
        Assert.Null(_prompts.Prompt);
        At(130);
        At(65);
        Assert.Null(_prompts.Prompt);
        Assert.False(_prompts.Dismiss());
    }

    [Fact]
    public void SeekingOutOfTheIntro_HidesTheAskPrompt()
    {
        With();
        At(61);

        At(300);

        Assert.Null(_prompts.Prompt);
    }

    [Fact]
    public void Always_SkipsTheIntroAtOnce_AndOffersItBack()
    {
        With(MarkerSettings.Default with { IntroSkipMode = MarkerSettings.Always });

        Assert.Equal(120, At(60));
        At(120);
        Assert.Equal(new SkipPrompt("Watch Intro", "Intro skipped", 0.05), _prompts.Prompt);

        Assert.Equal(60, _prompts.Select());
        Assert.Null(At(61));
        Assert.Null(_prompts.Prompt);
    }

    [Fact]
    public void Always_SettlesTheIntroWhenItsTimeRunsOut()
    {
        With(MarkerSettings.Default with { IntroSkipMode = MarkerSettings.Always });
        At(60);
        At(126, afterSeconds: 6);
        Assert.Null(_prompts.Prompt);

        Assert.Null(At(61));
        Assert.Null(_prompts.Prompt);
    }

    [Fact]
    public void Never_LeavesTheIntroAlone() =>
        Assert.True(With(MarkerSettings.Default with { IntroSkipMode = MarkerSettings.Never }).Update(61, MarkerActivity.Playing, TimeSpan.Zero) is null
            && _prompts.Prompt is null);

    [Fact]
    public void CreditsAndRecap_GetAPillThatStays()
    {
        With();

        At(10);
        Assert.Equal(new SkipPrompt("Skip Recap", null, null), _prompts.Prompt);
        At(30, afterSeconds: 20);
        Assert.Equal("Skip Recap", _prompts.Prompt?.Label);

        At(2550);
        Assert.Equal("Skip Credits", _prompts.Prompt?.Label);
        Assert.Equal(2600, _prompts.Select());
    }

    [Fact]
    public void CreditsTheProfileSkips_SkipThemselvesOnce()
    {
        With(MarkerSettings.Default with { AutoSkipCredits = true });

        Assert.Equal(2600, At(2510));
        Assert.Null(At(2520));
        Assert.Null(_prompts.Prompt);
    }

    [Fact]
    public void AResolvedIntro_IsNotOfferedAgain_InTheNextFileEither()
    {
        With();
        At(61);
        _prompts.Select();

        With();
        At(61);

        Assert.Null(_prompts.Prompt);
    }

    [Fact]
    public void TheIntro_IsTheVersionsProjection_TheOtherPartsEverySegment()
    {
        var version = new WatchFileVersion
        {
            Intro = new WatchMarker(60, 120),
            MarkerSegments =
            [
                new WatchMarkerSegment { Kind = WatchMarkerKinds.Recap, StartSeconds = 0, EndSeconds = 40 },
                new WatchMarkerSegment { Kind = WatchMarkerKinds.Intro, StartSeconds = 60, EndSeconds = 120 },
                new WatchMarkerSegment { Kind = WatchMarkerKinds.Intro, StartSeconds = 700, EndSeconds = 720 },
                new WatchMarkerSegment { Kind = WatchMarkerKinds.Credits, StartSeconds = 2500, EndSeconds = 2600 },
                new WatchMarkerSegment { Kind = WatchMarkerKinds.Credits, StartSeconds = 10, EndSeconds = 10 },
            ],
        };

        Assert.Equal([Recap, Intro, Credits], PlaybackMarker.Of(version));
    }

    [Fact]
    public void AKindWithoutSegments_IsTakenFromItsProjection()
    {
        var version = new WatchFileVersion
        {
            Credits = new WatchMarker(2500, 2600),
            MarkerSegments = [new WatchMarkerSegment { Kind = WatchMarkerKinds.Recap, StartSeconds = 0, EndSeconds = 40 }],
        };

        Assert.Equal([Recap, Credits], PlaybackMarker.Of(version));
    }

    [Fact]
    public void EveryPart_CarriesWhereItsKindCameFrom()
    {
        // The server keeps one origin per kind, so every occurrence of a kind shares it, as on the web (watchPageMarkers.ts).
        var version = new WatchFileVersion
        {
            Intro = new WatchMarker(60, 120) { Source = "plugin", Provider = "plugin:4:introdb", Confidence = 0.2 },
            Credits = new WatchMarker(1200, 1300) { Source = "scanner", Confidence = 0.9 },
            MarkerSegments =
            [
                new WatchMarkerSegment { Kind = WatchMarkerKinds.Recap, StartSeconds = 0, EndSeconds = 40 },
                new WatchMarkerSegment { Kind = WatchMarkerKinds.Intro, StartSeconds = 60, EndSeconds = 120 },
                new WatchMarkerSegment { Kind = WatchMarkerKinds.Credits, StartSeconds = 1200, EndSeconds = 1300 },
                new WatchMarkerSegment { Kind = WatchMarkerKinds.Credits, StartSeconds = 2500, EndSeconds = 2600 },
            ],
        };
        var scanned = new MarkerProvenance("scanner", null, 0.9);

        Assert.Equal(
            [
                Recap,
                Intro with { Provenance = new MarkerProvenance("plugin", "plugin:4:introdb", 0.2) },
                new PlaybackMarker(WatchMarkerKinds.Credits, 1200, 1300) { Provenance = scanned },
                Credits with { Provenance = scanned },
            ],
            PlaybackMarker.Of(version));
    }

    [Fact]
    public void TheLine_SaysHowConfidentAndWhereTheIntroCameFrom_AsTheProfileChose()
    {
        var intro = Intro with { Provenance = new MarkerProvenance("plugin", "plugin:4:introdb", 0.2) };
        _prompts.Reset([Recap, intro, Credits], MarkerSettings.Default with { ShowMarkerConfidence = true, ShowMarkerSource = true });

        At(61);

        Assert.Equal("Skip Intro", _prompts.Prompt?.Label);
        Assert.Equal("20% · TheIntroDB", _prompts.Prompt?.Provenance);
    }

    [Fact]
    public void TheLine_IsOff_UntilTheProfileTurnsItOn()
    {
        _prompts.Reset([Recap, Intro with { Provenance = new MarkerProvenance("scanner", null, 0.8) }, Credits], MarkerSettings.Default);

        At(61);

        Assert.Equal(new SkipPrompt("Skip Intro", null, 0), _prompts.Prompt);
        Assert.Null(_prompts.Prompt?.Provenance);
    }

    [Fact]
    public void TheUndoPill_CarriesTheIntrosLine_AboveItsCaption()
    {
        _prompts.Reset([Recap, Intro with { Provenance = new MarkerProvenance("manual", null, 1) }, Credits],
            MarkerSettings.Default with { IntroSkipMode = MarkerSettings.Always, ShowMarkerSource = true });

        At(60);
        At(120);

        Assert.Equal(new SkipPrompt("Watch Intro", "Intro skipped", 0.05) { Provenance = "Edited" }, _prompts.Prompt);
    }

    [Fact]
    public void ThePillOfAnotherPart_CarriesItsOwnLine_AndAnUnratedOneWithOnlyTheConfidenceOnHasNone()
    {
        var settings = MarkerSettings.Default with { ShowMarkerConfidence = true };
        _prompts.Reset([Recap with { Provenance = new MarkerProvenance("plugin", "plugin:4:introdb", null) },
            Intro, Credits with { Provenance = new MarkerProvenance("scanner", null, 0.734) }], settings);

        At(10);
        Assert.Equal(new SkipPrompt("Skip Recap", null, null), _prompts.Prompt);

        At(2550);
        Assert.Equal(new SkipPrompt("Skip Credits", null, null) { Provenance = "73%" }, _prompts.Prompt);
    }

    [Fact]
    public void AReadThatNamesAnotherSource_ChangesTheShowingLine()
    {
        var settings = MarkerSettings.Default with { ShowMarkerSource = true };
        _prompts.Reset([Recap, Intro with { Provenance = new MarkerProvenance("scanner", null, null) }, Credits], settings);
        At(61);
        Assert.Equal("Detected", _prompts.Prompt?.Provenance);

        var edited = Intro with { Provenance = new MarkerProvenance("manual", null, 1) };
        Assert.NotEqual(Intro, edited);
        _prompts.Reset([Recap, edited, Credits], settings);

        Assert.Equal("Edited", _prompts.Prompt?.Provenance);
    }

    [Fact]
    public void AnotherRead_KeepsAShowingCreditsPill_WithItsNewLine()
    {
        var settings = MarkerSettings.Default with { ShowMarkerSource = true };
        _prompts.Reset([Recap, Intro, Credits with { Provenance = new MarkerProvenance("scanner", null, null) }], settings);
        At(2550);

        _prompts.Reset([Recap, Intro, Credits with { Provenance = new MarkerProvenance("manual", null, 1) }], settings);

        // No blink until the next position: the pill goes on as it was.
        Assert.Equal(new SkipPrompt("Skip Credits", null, null) { Provenance = "Edited" }, _prompts.Prompt);
        Assert.Equal(2600, _prompts.Select());
    }

    [Fact]
    public void AnotherRead_KeepsTheUndoPillsTime_AndAPickedIntroIsNotOfferedAgain()
    {
        var settings = MarkerSettings.Default with { IntroSkipMode = MarkerSettings.Always, ShowMarkerConfidence = true };
        _prompts.Reset([Recap, Intro with { Provenance = new MarkerProvenance("scanner", null, 0.5) }, Credits], settings);
        At(60);
        At(121, afterSeconds: 2);

        _prompts.Reset([Recap, Intro with { Provenance = new MarkerProvenance("scanner", null, 0.9) }, Credits], settings);
        Assert.Equal(new SkipPrompt("Watch Intro", "Intro skipped", 0.4) { Provenance = "90%" }, _prompts.Prompt);

        Assert.Equal(60, _prompts.Select());
        _prompts.Reset([Recap, Intro with { Provenance = new MarkerProvenance("manual", null, 1) }, Credits], settings);
        Assert.Null(At(61));
        Assert.Null(_prompts.Prompt);
    }

    [Fact]
    public void AReadThatMovesAPartAwayFromThePosition_TakesItsPillAway_SoAPickNeverSkipsWhatIsNotMarked()
    {
        With();
        At(30);
        At(30, MarkerActivity.Paused);
        Assert.Equal("Skip Recap", _prompts.Prompt?.Label);

        _prompts.Reset([Recap with { StartSeconds = 35, EndSeconds = 50 }, Intro, Credits], MarkerSettings.Default);

        // 30 to 35 is no longer marked: a pick would seek to 50 over it.
        Assert.Null(_prompts.Prompt);
        Assert.Null(_prompts.Select());
        Assert.Null(At(30, MarkerActivity.Paused));
        Assert.Null(_prompts.Prompt);
    }

    [Fact]
    public void AReadThatMovesTheIntroAwayFromThePosition_TakesSkipIntroAway_UntilPlaybackReachesIt()
    {
        With();
        At(70);
        At(70, MarkerActivity.Paused);
        Assert.Equal("Skip Intro", _prompts.Prompt?.Label);

        _prompts.Reset([Recap, Intro with { StartSeconds = 80, EndSeconds = 130 }, Credits], MarkerSettings.Default);

        Assert.Null(_prompts.Prompt);
        Assert.Null(_prompts.Select());
        At(85);
        Assert.Equal("Skip Intro", _prompts.Prompt?.Label);
        Assert.Equal(130, _prompts.Select());
    }

    [Fact]
    public void TheUndoPill_OutlivesAReadThatMovesItsIntro_AndGoesBackToItsNewStart()
    {
        // The position is past the intro by design; its pill only ever seeks back, to where the intro now starts.
        var always = MarkerSettings.Default with { IntroSkipMode = MarkerSettings.Always };
        With(always);
        Assert.Equal(120, At(60));
        At(120);

        _prompts.Reset([Recap, Intro with { StartSeconds = 55, EndSeconds = 125 }, Credits], always);

        Assert.Equal("Watch Intro", _prompts.Prompt?.Label);
        Assert.Equal(55, _prompts.Select());
    }

    [Fact]
    public void AnIntroWithoutItsProjection_SaysNothingOfWhereItCameFrom()
    {
        // As the web: an occurrence takes its kind's single range's provenance, and there is none.
        var version = new WatchFileVersion
        {
            Credits = new WatchMarker(2400, 2600) { Source = "scanner" },
            MarkerSegments = [new WatchMarkerSegment { Kind = WatchMarkerKinds.Intro, StartSeconds = 60, EndSeconds = 120 }],
        };

        Assert.Equal([Intro, new PlaybackMarker(WatchMarkerKinds.Credits, 2400, 2600) { Provenance = new MarkerProvenance("scanner", null, null) }],
            PlaybackMarker.Of(version));
    }

    [Fact]
    public void AMarkersAnswer_GivesEachKindsProvenance_AndNoConfidenceWhereNobodyRatedIt()
    {
        var answer = System.Text.Json.JsonSerializer.Deserialize("""
            { "file_id": "42", "intro": { "start_seconds": 60, "end_seconds": 120, "source": "manual", "confidence": 1 },
              "credits": { "start_seconds": 2500, "end_seconds": 2600, "source": "scanner", "algorithm": "chromaprint:v4" }, "recap": {}, "preview": {},
              "marker_segments": [ { "kind": "intro", "start_seconds": 60, "end_seconds": 120 },
                                   { "kind": "credits", "start_seconds": 2500, "end_seconds": 2600 } ] }
            """, PlaybackJsonContext.Default.FileMarkers)!;

        Assert.Equal(
            [
                Intro with { Provenance = new MarkerProvenance("manual", null, 1) },
                Credits with { Provenance = new MarkerProvenance("scanner", null, null) },
            ],
            PlaybackMarker.Of(answer.AsVersion("42")));
    }

    [Theory]
    [InlineData(false, 2600.0)]
    [InlineData(true, 2560.0)]
    public void TheNextEpisode_IsOfferedFromTheCreditsThatEndTheTitle_OrFromItsPreview(bool fromPreview, double expected)
    {
        PlaybackMarker[] markers =
        [
            new(WatchMarkerKinds.Credits, 1200, 1300),
            new(WatchMarkerKinds.Preview, 2560, 2600),
            new(WatchMarkerKinds.Credits, 2600, 2699.5),
        ];

        Assert.Equal(expected, (MarkerSettings.Default with { AutoPlayNextPreview = fromPreview }).AutoplayStart(markers, 2700));
    }

    [Fact]
    public void CreditsThatDoNotEndTheTitle_OfferNoNextEpisode()
    {
        Assert.Null(MarkerSettings.Default.AutoplayStart([new PlaybackMarker(WatchMarkerKinds.Credits, 1200, 1300)], 2700));
        Assert.Equal(1200, MarkerSettings.Default.AutoplayStart([new PlaybackMarker(WatchMarkerKinds.Credits, 1200, 1300)], 0));
    }
}
