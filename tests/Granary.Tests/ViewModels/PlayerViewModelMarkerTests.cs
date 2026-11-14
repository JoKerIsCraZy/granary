using Granary.Core.Abstractions;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>The skip pills of the player page, fed by the playback's markers and the engine's position.</summary>
public sealed class PlayerViewModelMarkerTests
{
    private static readonly PlaybackMarker Recap = new(WatchMarkerKinds.Recap, 0, 40);
    private static readonly PlaybackMarker Intro = new(WatchMarkerKinds.Intro, 60, 120);
    private static readonly PlaybackMarker Credits = new(WatchMarkerKinds.Credits, 2500, 2600);

    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<FakeServerPlayback> _playbacks = [];

    private FakeServerPlayback Playback => _playbacks[^1];

    private async Task<PlayerViewModel> PlayingAsync(MarkerSettings? settings = null)
    {
        var viewModel = new PlayerViewModel(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () =>
        {
            var playback = new FakeServerPlayback
            {
                DurationSeconds = 2700,
                Preferences = PlaybackPreferences.Default with { Markers = settings ?? MarkerSettings.Default },
            };
            _playbacks.Add(playback);
            return playback;
        }, new PlaybackStops(), _time);
        await viewModel.StartAsync(PlayRequest.Resume("episode-1"));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        Playback.RaiseMarkers([Intro, Credits]);
        return viewModel;
    }

    private void At(double seconds, bool paused = false, double afterSeconds = 0.25)
    {
        _time.Advance(TimeSpan.FromSeconds(afterSeconds));
        _engine.RaiseState(new PlaybackSnapshot(true, paused, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(2700), null, HasStarted: true));
    }

    [Fact]
    public async Task TheChapters_FollowThePosition_AndARowSeeksToItsStart()
    {
        var viewModel = await PlayingAsync();
        Assert.False(viewModel.HasChapters);

        Playback.RaiseChapters([new PlaybackChapter("Opening", 0, 600, null), new PlaybackChapter("Heist", 600, 2700, null)]);
        At(30);

        Assert.True(viewModel.HasChapters);
        Assert.Equal(2, viewModel.ChapterMarks.Count);
        Assert.Equal([true, false], viewModel.Chapters.Select(c => c.IsCurrent));
        var rows = viewModel.Chapters;
        At(31);
        Assert.Same(rows, viewModel.Chapters);

        At(601);
        Assert.Equal([false, true], viewModel.Chapters.Select(c => c.IsCurrent));

        viewModel.SeekToChapter(0);
        Assert.Equal([0], Playback.Seeks);
    }

    // Each from the position alone: no seek before it.
    [Theory]
    [InlineData(900, false, 600)]
    [InlineData(600.5, false, 0)]
    [InlineData(0, true, 600)]
    [InlineData(900, true, 2000)]
    public async Task TheChapterKeys_GoToTheNextChapter_AndBackToTheStartOfThisOne_OrWithinASecondOfIt_TheOneBefore(
        double seconds, bool forward, double expected)
    {
        var viewModel = await PlayingAsync();
        Playback.RaiseChapters([new PlaybackChapter("Opening", 0, 600, null), new PlaybackChapter("Heist", 600, 2000, null),
            new PlaybackChapter("Credits", 2000, 2700, null)]);
        At(seconds);

        viewModel.SeekToAdjacentChapter(forward);

        Assert.Equal([expected], Playback.Seeks);
    }

    [Fact]
    public async Task AChapterKeyPressedAgain_GoesOnFromTheSeekOnItsWay()
    {
        var viewModel = await PlayingAsync();
        Playback.RaiseChapters([new PlaybackChapter("Opening", 0, 600, null), new PlaybackChapter("Heist", 600, 2000, null),
            new PlaybackChapter("Credits", 2000, 2700, null)]);
        At(100);

        viewModel.SeekToAdjacentChapter(forward: true);
        viewModel.SeekToAdjacentChapter(forward: true);
        viewModel.SeekToAdjacentChapter(forward: false);

        Assert.Equal([600, 2000, 600], Playback.Seeks);
    }

    [Fact]
    public async Task TheChapterKeys_DoNothing_PastTheLastAndFirstChapter_NorWithoutChapters()
    {
        var viewModel = await PlayingAsync();
        At(100);
        viewModel.SeekToAdjacentChapter(forward: true);
        viewModel.SeekToAdjacentChapter(forward: false);

        Playback.RaiseChapters([new PlaybackChapter("Opening", 0, 600, null), new PlaybackChapter("Heist", 600, 2700, null)]);
        At(0.5);
        viewModel.SeekToAdjacentChapter(forward: false);
        At(1000);
        viewModel.SeekToAdjacentChapter(forward: true);

        Assert.Empty(Playback.Seeks);
    }

    [Fact]
    public async Task InTheIntro_ThePillOffersToSkipIt_AndSkippingSeeksToItsEnd()
    {
        var viewModel = await PlayingAsync();
        Assert.Equal([Intro, Credits], viewModel.Markers);

        At(61);
        Assert.True(viewModel.HasSkipPrompt);
        Assert.Equal("Skip Intro", viewModel.SkipLabel);
        At(63, afterSeconds: 2);
        Assert.Equal(0.4, viewModel.SkipProgress, 2);

        viewModel.SkipCommand.Execute(null);

        Assert.Equal([120], Playback.Seeks);
        Assert.False(viewModel.HasSkipPrompt);
    }

    [Fact]
    public async Task Always_SkipsTheIntroItself_AndOffersItBack()
    {
        var viewModel = await PlayingAsync(MarkerSettings.Default with { IntroSkipMode = MarkerSettings.Always });

        At(60.5);

        Assert.Equal([120], Playback.Seeks);
        Assert.Equal("Watch Intro", viewModel.SkipLabel);
        Assert.Equal("Intro skipped", viewModel.SkipCaption);

        viewModel.SkipCommand.Execute(null);
        Assert.Equal([120, 60], Playback.Seeks);
    }

    [Fact]
    public async Task WatchIntro_PressedAfterSeekingBackBeforeTheIntro_DoesNotSeekForward()
    {
        var viewModel = await PlayingAsync(MarkerSettings.Default with { IntroSkipMode = MarkerSettings.Always });
        At(60.5);
        At(120);
        viewModel.SeekTo(30);
        At(30);
        Assert.True(viewModel.HasSkipPrompt);
        Assert.Equal("Watch Intro", viewModel.SkipLabel);

        viewModel.SkipCommand.Execute(null);

        Assert.Equal([120, 30], Playback.Seeks);
        Assert.False(viewModel.HasSkipPrompt);
    }

    [Fact]
    public async Task ThePill_PressedWhileASeekOutOfItsPartIsOnItsWay_SkipsNothing()
    {
        var viewModel = await PlayingAsync();
        At(2550);
        Assert.Equal("Skip Credits", viewModel.SkipLabel);

        viewModel.SeekTo(1000);
        viewModel.SkipCommand.Execute(null);

        Assert.Equal([1000], Playback.Seeks);
        Assert.False(viewModel.HasSkipPrompt);
    }

    [Fact]
    public async Task AReadWhileASeekBackIsOnItsWay_TakesAwayAPillWhosePartNoLongerHoldsWherePlaybackGoes()
    {
        var viewModel = await PlayingAsync();
        Playback.RaiseMarkers([Recap, Intro, Credits]);
        At(30, paused: true);
        Assert.Equal("Skip Recap", viewModel.SkipLabel);

        viewModel.SeekTo(10);
        // Read again before the player reports the seek: the recap now starts at 20, which holds 30 but not 10.
        Playback.RaiseMarkers([Recap with { StartSeconds = 20 }, Intro, Credits]);

        Assert.False(viewModel.HasSkipPrompt);
        viewModel.SkipCommand.Execute(null);
        Assert.Equal([10], Playback.Seeks);
    }

    [Fact]
    public async Task ThePill_ShowsWhereItsMarkerCameFrom_AndARereadThatNamesAnotherSourceChangesIt()
    {
        var viewModel = await PlayingAsync(MarkerSettings.Default with { ShowMarkerConfidence = true, ShowMarkerSource = true });
        At(30);
        Assert.Equal("", viewModel.SkipProvenance);

        Playback.RaiseMarkers([Intro with { Provenance = new MarkerProvenance("plugin", "plugin:4:introdb", 0.2) }, Credits]);
        At(61);
        Assert.Equal("Skip Intro", viewModel.SkipLabel);
        Assert.Equal("20% · TheIntroDB", viewModel.SkipProvenance);

        Playback.RaiseMarkers([Intro with { Provenance = new MarkerProvenance("manual", null, 1) }, Credits]);
        Assert.Equal("100% · Edited", viewModel.SkipProvenance);

        Playback.RaiseMarkers([Intro, Credits]);
        Assert.True(viewModel.HasSkipPrompt);
        Assert.Equal("", viewModel.SkipProvenance);
    }

    [Fact]
    public async Task Esc_TurnsTheIntroPillAway_OnlyWhenOneShows()
    {
        var viewModel = await PlayingAsync();
        Assert.False(viewModel.DismissSkipPrompt());

        At(61);

        Assert.True(viewModel.DismissSkipPrompt());
        Assert.False(viewModel.HasSkipPrompt);
        At(62);
        Assert.False(viewModel.HasSkipPrompt);
    }

    [Fact]
    public async Task Esc_LeavesAHiddenPillAlone_AndItsTimeStandsMeanwhile()
    {
        var viewModel = await PlayingAsync();
        At(61);
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Reconnecting));

        Assert.False(viewModel.DismissSkipPrompt());
        _time.Advance(TimeSpan.FromSeconds(30));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));

        Assert.True(viewModel.HasSkipPrompt);
        Assert.Equal(0, viewModel.SkipProgress, 2);
    }

    [Fact]
    public async Task TheCreditsPill_Stays_AndShowsNoTime()
    {
        var viewModel = await PlayingAsync();

        At(2510);
        At(2540, afterSeconds: 30);

        Assert.True(viewModel.HasSkipPrompt);
        Assert.Equal("Skip Credits", viewModel.SkipLabel);
        Assert.Equal(0, viewModel.SkipProgress);
    }

    [Fact]
    public async Task ThePill_HidesWhilePlaybackCannotSeek_AndAfterTheEnd()
    {
        var viewModel = await PlayingAsync();
        At(61);

        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Reconnecting));
        Assert.False(viewModel.HasSkipPrompt);

        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        Assert.True(viewModel.HasSkipPrompt);

        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Ended));
        Assert.False(viewModel.HasSkipPrompt);
    }

    [Fact]
    public async Task ANewTitle_StartsWithNoMarkersAndNothingResolved()
    {
        var viewModel = await PlayingAsync();
        At(61);
        viewModel.SkipCommand.Execute(null);

        await viewModel.StartAsync(PlayRequest.Resume("episode-2"));
        Assert.Empty(viewModel.Markers);
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        Playback.RaiseMarkers([Intro]);
        At(61);

        Assert.Equal("Skip Intro", viewModel.SkipLabel);
        Assert.True(viewModel.HasSkipPrompt);
    }
}
