using Granary.Core.Abstractions;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>The skip pills of the player page, fed by the playback's markers and the engine's position.</summary>
public sealed class PlayerViewModelMarkerTests
{
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
