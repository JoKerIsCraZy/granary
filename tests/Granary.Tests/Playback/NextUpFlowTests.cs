using Granary.Core.Api.Catalog;
using Granary.Core.Playback;

namespace Granary.Tests.Playback;

/// <summary>The way to the next episode: the card at the credits, the screen before the end, the countdown at it.</summary>
public sealed class NextUpFlowTests
{
    private const double Length = 2100;
    private const double CreditsStart = 2040;

    private static readonly NextEpisode Next = new("episode-1-4", 1, 4, "Schrei der Verlorenen");

    private readonly NextUpFlow _flow = new();
    private TimeSpan _now;

    private NextUpFlow Episode(MarkerSettings? settings = null, bool gated = false, double? credits = CreditsStart)
    {
        _flow.Start(isEpisode: true, settings ?? MarkerSettings.Default, gated);
        _flow.TakeCredits(credits);
        _flow.TakeNext(Next, _now);
        return _flow;
    }

    // Plays from one second to another in steps of a quarter second, as the engine reports.
    private void Play(double from, double to)
    {
        for (double position = from; position <= to; position += 0.25)
        {
            _now += TimeSpan.FromSeconds(0.25);
            _flow.Update(position, Length, _now);
        }
    }

    private NextUpStep After(double seconds)
    {
        _now += TimeSpan.FromSeconds(seconds);
        return _flow.Tick(_now);
    }

    [Fact]
    public void TheCredits_BringTheCard_CountingThePlaybackLeft()
    {
        Episode();

        Play(2030, 2039.75);
        Assert.Equal(NextUpView.None, _flow.View);

        Play(2039, 2045);
        Assert.Equal(NextUpView.Card, _flow.View);
        Assert.Equal(55, _flow.Seconds);
    }

    [Fact]
    public void WithoutCredits_TheCardComesThePromptSecondsBeforeTheEnd_AndNotAtAllForZero()
    {
        Episode(MarkerSettings.Default with { NextUpPromptSeconds = 60 }, credits: null);
        Play(2035, 2041);
        Assert.Equal(NextUpView.Card, _flow.View);

        // The default 30 s fall on the screen's point: the screen comes straight away.
        Episode(credits: null);
        Play(2065, 2071);
        Assert.Equal(NextUpView.Screen, _flow.View);

        Episode(MarkerSettings.Default with { NextUpPromptSeconds = 0 }, credits: null);
        Play(2035, 2069);
        Assert.Equal(NextUpView.None, _flow.View);
    }

    [Fact]
    public void ResumingOrSeekingIntoTheCredits_BringsNoCard()
    {
        Episode();

        _flow.Update(2050, Length, _now);
        Play(2050.25, 2055);
        Assert.Equal(NextUpView.None, _flow.View);

        Episode();
        Play(1000, 1001);
        _flow.Update(2050, Length, _now);
        Assert.Equal(NextUpView.None, _flow.View);
    }

    [Fact]
    public void ThirtySecondsBeforeTheEnd_TheScreenTakesOver_WithoutACountdownYet()
    {
        Episode();
        Play(2039, 2069.75);
        Assert.Equal(NextUpView.Card, _flow.View);

        Play(2070, 2071);

        Assert.Equal(NextUpView.Screen, _flow.View);
        Assert.Null(_flow.Seconds);
    }

    [Fact]
    public void AtTheEnd_TheScreenCountsTenSeconds_AndPlaysTheNextEpisode()
    {
        Episode();
        Play(2065, 2099);

        Assert.Equal(NextUpStep.None, _flow.End(_now));
        Assert.Equal(NextUpView.Screen, _flow.View);
        Assert.Equal(10, _flow.Seconds);

        Assert.Equal(NextUpStep.None, After(2.5));
        Assert.Equal(8, _flow.Seconds);
        Assert.Equal(NextUpStep.PlayNext, After(7.5));
    }

    [Fact]
    public void Cancel_KeepsThePromptsAwayUntilTheEnd_WhichThenWaitsForTheViewer()
    {
        Episode();
        Play(2039, 2045);

        _flow.Dismiss(_now);
        Play(2045.25, 2075);
        Assert.Equal(NextUpView.None, _flow.View);

        _flow.End(_now);
        Assert.Equal(NextUpView.Screen, _flow.View);
        Assert.False(_flow.IsCountingDown);
        Assert.Equal(NextUpStep.None, After(30));
    }

    [Fact]
    public void APauseDuringTheCard_StopsItsCountdown_AndTheEndWaits()
    {
        Episode();
        Play(2039, 2045);

        _flow.Paused(_now);

        Assert.Equal(NextUpView.Card, _flow.View);
        Assert.Null(_flow.Seconds);
        _flow.End(_now);
        Assert.False(_flow.IsCountingDown);
    }

    [Fact]
    public void AShortSkipAcrossThePoint_BringsNoCard()
    {
        Episode();
        Play(2030, 2037);

        _flow.Sought(_now);
        Play(2041, 2045);

        Assert.Equal(NextUpView.None, _flow.View);
    }

    [Fact]
    public void ASeekDuringTheCard_CancelsIt()
    {
        Episode();
        Play(2039, 2045);

        _flow.Sought(_now);

        Assert.Equal(NextUpView.None, _flow.View);
    }

    [Fact]
    public void AutoPlayOff_OrTheGate_ShowEverythingWithoutCountingDown()
    {
        Episode(MarkerSettings.Default with { AutoPlayNext = false });
        Play(2039, 2045);
        Assert.Equal(NextUpView.Card, _flow.View);
        Assert.Null(_flow.Seconds);
        _flow.End(_now);
        Assert.False(_flow.IsCountingDown);

        Episode(gated: true);
        Play(2039, 2045);
        Assert.Null(_flow.Seconds);
        _flow.End(_now);
        Assert.False(_flow.IsCountingDown);
    }

    [Fact]
    public void TurningAutoPlayOnAtTheEnd_StartsTheCountdown_AndOffStopsIt()
    {
        Episode(MarkerSettings.Default with { AutoPlayNext = false });
        _flow.End(_now);

        _flow.SetAutoPlayNext(true, _now);
        Assert.True(_flow.IsCountingDown);

        _flow.SetAutoPlayNext(false, _now);
        Assert.False(_flow.IsCountingDown);
        Assert.Equal(NextUpStep.None, After(20));
    }

    [Fact]
    public void ANextEpisodeFoundAfterTheEnd_StillCountsDown()
    {
        _flow.Start(isEpisode: true, MarkerSettings.Default, gated: false);
        _flow.End(_now);
        Assert.Equal(NextUpView.Screen, _flow.View);
        Assert.Null(_flow.Next);

        _flow.TakeNext(Next, _now);

        Assert.True(_flow.IsCountingDown);
    }

    [Fact]
    public void TheLastEpisode_GetsTheScreenWithoutACard_AndAMovieLeavesAtItsEnd()
    {
        _flow.Start(isEpisode: true, MarkerSettings.Default, gated: false);
        _flow.TakeCredits(CreditsStart);
        _flow.TakeNext(null, _now);
        Play(2039, 2045);
        Assert.Equal(NextUpView.None, _flow.View);
        Play(2045.25, 2071);
        Assert.Equal(NextUpView.Screen, _flow.View);

        _flow.Start(isEpisode: false, MarkerSettings.Default, gated: false);
        Play(2039, 2075);
        Assert.Equal(NextUpView.None, _flow.View);
        Assert.Equal(NextUpStep.Leave, _flow.End(_now));
    }

    [Fact]
    public void TheNextEpisode_IsTheSmallestSeasonAndEpisodeAfterThisOne()
    {
        Episode[] pool =
        [
            new() { ContentId = "s2e1", SeasonNumber = 2, EpisodeNumber = 1 },
            new() { ContentId = "s1e3", SeasonNumber = 1, EpisodeNumber = 3 },
            new() { ContentId = "s1e4", SeasonNumber = 1, EpisodeNumber = 4 },
        ];

        Assert.Equal("s1e4", NextEpisode.After(pool, 1, 3)?.ContentId);
        Assert.Equal("s2e1", NextEpisode.After(pool, 1, 4)?.ContentId);
        Assert.Null(NextEpisode.After(pool, 2, 1));
    }

    [Fact]
    public void TheStreak_GatesAtThree_AndAViewerActionResetsIt()
    {
        var streak = new AutoPlayStreak();
        streak.RecordAutomatic();
        streak.RecordAutomatic();
        Assert.False(streak.IsGated);

        streak.RecordAutomatic();
        Assert.True(streak.IsGated);

        streak.Reset();
        Assert.False(streak.IsGated);
    }
}
