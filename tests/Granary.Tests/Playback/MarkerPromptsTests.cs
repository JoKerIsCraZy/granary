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
}
