using System.ComponentModel;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>What the player holds of the stream, for the seek bar, in the time the bar shows.</summary>
public sealed class PlayerViewModelBufferedTests
{
    private readonly FakePlaybackEngine _engine = new();
    private readonly List<FakeServerPlayback> _playbacks = [];

    private FakeServerPlayback Playback => _playbacks[^1];

    private PlayerViewModel CreateViewModel() => new(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () =>
    {
        var playback = new FakeServerPlayback();
        _playbacks.Add(playback);
        return playback;
    }, new PlaybackStops());

    [Fact]
    public async Task TheRangesThePlayerHolds_ShowOnTheSeekBar()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Direct("http://127.0.0.1:8080/clip.mp4"));

        _engine.RaiseState(Playing() with { Buffered = BufferedRanges.Of([new(0, 20), new(24, 28)]) });

        Assert.Equal([new BufferedRange(0, 20), new BufferedRange(24, 28)], viewModel.Buffered);
    }

    [Fact]
    public async Task AServerStreamsRanges_AreInTheTitlesOwnTime_AsItsPosition()
    {
        // A copy remux that starts at 120 s of the title (docs/api-contract.md, 8.6).
        var viewModel = await PlayServerTitleAsync(offset: 120, duration: 5400);

        _engine.RaiseState(Playing() with { Buffered = BufferedRanges.Of([new(0, 30), new(60, 90)]) });

        Assert.Equal(132.5, viewModel.PositionSeconds);
        Assert.Equal([new BufferedRange(120, 150), new BufferedRange(180, 210)], viewModel.Buffered);
    }

    [Fact]
    public async Task Ranges_KeepWithinTheTitlesLength()
    {
        var viewModel = await PlayServerTitleAsync(offset: 120, duration: 140);

        _engine.RaiseState(Playing() with { Buffered = BufferedRanges.Of([new(0, 10), new(15, 60)]) });

        Assert.Equal([new BufferedRange(120, 130), new BufferedRange(135, 140)], viewModel.Buffered);
    }

    [Fact]
    public async Task WithoutALength_NoRangesShow()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Direct("http://127.0.0.1:8080/live.ts"));

        _engine.RaiseState(Playing() with { Duration = null, Buffered = BufferedRanges.Of([new(0, 20)]) });

        Assert.Empty(viewModel.Buffered);
    }

    [Fact]
    public async Task AStreamOnItsWay_ShowsNoRanges()
    {
        // A reload or replan: whatever the engine says before the new stream plays is not that stream's.
        var viewModel = await PlayServerTitleAsync(offset: 0, duration: 600);
        _engine.RaiseState(Playing() with { Buffered = BufferedRanges.Of([new(0, 30)]) });

        _engine.Load(new MediaSource(new Uri("http://127.0.0.1:9/stream"), []));
        _engine.RaiseState(Playing() with { IsLoaded = false, HasStarted = false, Buffered = BufferedRanges.Of([new(0, 30)]) });

        Assert.Empty(viewModel.Buffered);
    }

    [Fact]
    public async Task AReconnect_HidesTheRanges()
    {
        var viewModel = await PlayServerTitleAsync(offset: 0, duration: 600);
        _engine.RaiseState(Playing() with { Buffered = BufferedRanges.Of([new(0, 30)]) });

        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Reconnecting));

        Assert.Empty(viewModel.Buffered);
    }

    [Fact]
    public async Task TheEnginesClearedRanges_ClearTheBar()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Direct("http://127.0.0.1:8080/clip.mp4"));
        _engine.RaiseState(Playing() with { Buffered = BufferedRanges.Of([new(0, 20)]) });

        // The engine's stop: the file ended, and the engine is idle.
        _engine.RaiseState(PlaybackSnapshot.Idle);

        Assert.Empty(viewModel.Buffered);
    }

    [Fact]
    public async Task Leaving_ClearsTheRanges()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Direct("http://127.0.0.1:8080/clip.mp4"));
        _engine.RaiseState(Playing() with { Buffered = BufferedRanges.Of([new(0, 20)]) });

        viewModel.Leave();

        Assert.Equal(1, _engine.Stops);
        Assert.Empty(viewModel.Buffered);
    }

    [Fact]
    public async Task AnotherTitle_StartsWithoutTheRangesOfTheOneBefore()
    {
        var viewModel = await PlayServerTitleAsync(offset: 0, duration: 600);
        _engine.RaiseState(Playing() with { Buffered = BufferedRanges.Of([new(0, 30)]) });

        await viewModel.StartAsync(PlayRequest.Resume("movie-2"));

        Assert.Empty(viewModel.Buffered);
    }

    [Fact]
    public async Task ThePositionMovingOn_DoesNotSetTheRangesAgain()
    {
        var viewModel = await PlayServerTitleAsync(offset: 120, duration: 5400);
        var held = BufferedRanges.Of([new(0, 30)]);
        _engine.RaiseState(Playing() with { Buffered = held });
        var shown = viewModel.Buffered;
        int changes = 0;
        ((INotifyPropertyChanged)viewModel).PropertyChanged += (_, e) => changes += e.PropertyName == nameof(PlayerViewModel.Buffered) ? 1 : 0;

        _engine.RaiseState(Playing() with { Position = TimeSpan.FromSeconds(13), Buffered = held });
        _engine.RaiseState(Playing() with { Position = TimeSpan.FromSeconds(13.5), Buffered = BufferedRanges.Of([new(0, 30)]) });

        Assert.Equal(0, changes);
        Assert.Same(shown, viewModel.Buffered);
    }

    private async Task<PlayerViewModel> PlayServerTitleAsync(double offset, double duration)
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        Playback.TimelineOffsetSeconds = offset;
        Playback.DurationSeconds = duration;
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        return viewModel;
    }

    private static PlaybackSnapshot Playing() =>
        new(true, false, TimeSpan.FromSeconds(12.5), TimeSpan.FromSeconds(600), "d3d11va", HasStarted: true);
}
