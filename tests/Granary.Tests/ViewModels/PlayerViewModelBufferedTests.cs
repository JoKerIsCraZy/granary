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
    public async Task ANewPlan_HidesTheRangesOfTheStreamBefore_UntilTheEngineLoadsIt()
    {
        // A seek outside a transcode's window: the server re-anchors it at 1800 s (docs/api-contract.md, 8.6). The plan is
        // taken before its stream is loaded, and meanwhile the engine still reports the stream before, which plays on.
        var viewModel = await PlayServerTitleAsync(offset: 120, duration: 5400);
        var before = BufferedRanges.Of([new(0, 30)]);
        _engine.RaiseState(Playing() with { Buffered = before });
        Assert.Equal([new BufferedRange(120, 150)], viewModel.Buffered);

        Playback.TimelineOffsetSeconds = 1800;
        _engine.RaiseState(Playing() with { Position = TimeSpan.FromSeconds(13), Buffered = before });
        Assert.Empty(viewModel.Buffered);
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        Assert.Empty(viewModel.Buffered);

        _engine.Load(new MediaSource(new Uri("http://127.0.0.1:9/reanchored"), []));
        _engine.RaiseState(Playing() with { Position = TimeSpan.FromSeconds(0.5), Buffered = BufferedRanges.Of([new(0, 10)]) });

        Assert.Equal([new BufferedRange(1800, 1810)], viewModel.Buffered);
    }

    [Fact]
    public async Task ANewPlanBeforeTheStreamStarted_HidesTheRangesOfTheStreamBefore()
    {
        // A seek or another quality in the first moments, before the stream showed a frame: the offset the load began with,
        // not the one its first frame came with, tells its stream from the new plan's.
        var viewModel = await PlayServerTitleAsync(offset: 120, duration: 5400);
        _engine.RaiseState(Playing() with { HasStarted = false });

        Playback.TimelineOffsetSeconds = 1800;
        _engine.RaiseState(Playing() with { Buffered = BufferedRanges.Of([new(0, 30)]) });

        Assert.Empty(viewModel.Buffered);
    }

    [Fact]
    public async Task ASnapshotOfAnEarlierLoad_ArrivingLate_ShowsNoRanges()
    {
        // The engine reports what its cache holds from a timer's thread: a read of the load before, numbered before the
        // new load's first snapshot, can reach the UI thread after it.
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Direct("http://127.0.0.1:8080/clip.mp4"));
        long first = _engine.LoadId;
        long read = _engine.Current.Sequence + 1;
        long second = _engine.Load(new MediaSource(new Uri("http://127.0.0.1:8080/clip.mp4"), []));
        _engine.RaiseState(Playing() with { LoadId = second, IsLoaded = false, HasStarted = false, Sequence = read + 1 });

        _engine.RaiseState(Playing() with { LoadId = first, Buffered = BufferedRanges.Of([new(0, 20)]), Sequence = read });

        Assert.Empty(viewModel.Buffered);
        _engine.RaiseState(Playing() with { LoadId = second, Buffered = BufferedRanges.Of([new(0, 5)]) });
        Assert.Equal([new BufferedRange(0, 5)], viewModel.Buffered);
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

        // A stop or a failure: mpv closed the file, and the engine is idle.
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
    public async Task ThePositionMovingOn_DoesNotMapTheRangesAgain()
    {
        // Four position updates a second: the same ranges, or equal ones read anew, are neither moved again nor shown anew.
        var viewModel = await PlayServerTitleAsync(offset: 120, duration: 5400);
        var held = BufferedRanges.Of([new(0, 30)]);
        _engine.RaiseState(Playing() with { Buffered = held });
        var shown = viewModel.Buffered;
        int mappings = viewModel.BufferedMappings;
        int changes = 0;
        ((INotifyPropertyChanged)viewModel).PropertyChanged += (_, e) => changes += e.PropertyName == nameof(PlayerViewModel.Buffered) ? 1 : 0;

        _engine.RaiseState(Playing() with { Position = TimeSpan.FromSeconds(13), Buffered = held });
        _engine.RaiseState(Playing() with { Position = TimeSpan.FromSeconds(13.5), Buffered = BufferedRanges.Of([new(0, 30)]) });

        Assert.Equal(mappings, viewModel.BufferedMappings);
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
