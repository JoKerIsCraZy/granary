using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

public sealed class PlayerViewModelTests
{
    private readonly FakePlaybackEngine _engine = new();
    private readonly RecordingNavigation _navigation = new();

    private PlayerViewModel CreateViewModel() =>
        new(_engine, new ImmediateDispatcher(), _navigation, () => throw new InvalidOperationException("A direct source needs no server."), new PlaybackStops());

    [Fact]
    public async Task Direct_LoadsAnHttpUrlAsIs()
    {
        var viewModel = CreateViewModel();

        await viewModel.StartAsync(PlayRequest.Direct(" https://nas.example/media/clip.mkv "));

        var source = Assert.Single(_engine.Loaded);
        Assert.Equal("https://nas.example/media/clip.mkv", source.Url.AbsoluteUri);
        Assert.Empty(source.Headers);
        Assert.Equal(PlayerPhase.Preparing, viewModel.Phase);
        Assert.Equal("clip.mkv", viewModel.Title);
    }

    [Fact]
    public async Task Direct_TreatsAnythingElseAsALocalPath()
    {
        var viewModel = CreateViewModel();

        await viewModel.StartAsync(PlayRequest.Direct("\"C:\\Videos\\movie.mkv\""));

        var source = Assert.Single(_engine.Loaded);
        Assert.True(source.Url.IsFile);
        Assert.Equal(@"C:\Videos\movie.mkv", source.Url.LocalPath);
    }

    [Theory]
    [InlineData(@"\\nas\share\movie.mkv")]
    [InlineData("//nas/share/movie.mkv")]
    public async Task Direct_RefusesNetworkPaths(string path)
    {
        // Opening one would make Windows sign in to that host with the user's account.
        var viewModel = CreateViewModel();

        await viewModel.StartAsync(PlayRequest.Direct(path));

        Assert.Empty(_engine.Loaded);
        Assert.True(viewModel.IsFailed);
    }

    [Fact]
    public async Task TheFirstFrame_TurnsPreparingIntoPlaying()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Direct(@"C:\Videos\movie.mkv"));

        _engine.RaiseState(Loaded() with { HasStarted = false });
        Assert.True(viewModel.IsPreparing);

        _engine.RaiseState(Loaded());

        Assert.Equal(PlayerPhase.Playing, viewModel.Phase);
        Assert.True(viewModel.IsPlaying);
        Assert.True(viewModel.CanSeek);
    }

    [Fact]
    public void StateChanges_UpdateTheTransportProperties()
    {
        var viewModel = CreateViewModel();

        _engine.RaiseState(Loaded() with { IsPaused = true, IsBuffering = true, Volume = 35, IsMuted = true });

        Assert.True(viewModel.IsPaused);
        Assert.True(viewModel.IsBuffering);
        Assert.Equal(12.5, viewModel.PositionSeconds);
        Assert.Equal(30, viewModel.DurationSeconds);
        Assert.Equal(35, viewModel.Volume);
        Assert.True(viewModel.IsMuted);
    }

    [Fact]
    public async Task AStateFromBeforeTheStop_ArrivingAfterIt_ChangesNothing()
    {
        // The engine raises from mpv's event thread, from its cache reads on a timer's and from a load: a cache read taken
        // before a stop can reach the page after the stop's idle state, which nothing would set right again.
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Direct("http://127.0.0.1:8080/clip.mp4"));
        _engine.RaiseState(Loaded());
        long before = _engine.Current.Sequence;

        _engine.RaiseState(PlaybackSnapshot.Idle with { Sequence = before + 2 });
        _engine.RaiseState(Loaded() with { Buffered = BufferedRanges.Of([new(0, 20)]), Sequence = before + 1 });

        Assert.False(viewModel.CanSeek);
        Assert.Empty(viewModel.Buffered);
        Assert.Equal(0, viewModel.PositionSeconds);
    }

    [Fact]
    public void AnOlderPosition_ArrivingAfterANewerOne_DoesNotMoveTheBarBack()
    {
        var viewModel = CreateViewModel();

        _engine.RaiseState(Loaded() with { Position = TimeSpan.FromSeconds(20), Sequence = 2 });
        _engine.RaiseState(Loaded() with { Position = TimeSpan.FromSeconds(12.5), IsPaused = true, Sequence = 1 });

        Assert.Equal(20, viewModel.PositionSeconds);
        Assert.False(viewModel.IsPaused);
    }

    [Fact]
    public async Task TheEnd_IsEnded()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Direct(@"C:\Videos\movie.mkv"));

        _engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.EndOfFile, null));

        Assert.True(viewModel.IsEnded);
    }

    [Fact]
    public async Task AFailure_ShowsWhy()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Direct(@"C:\Videos\movie.mkv"));

        _engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.Error, "loading failed"));

        Assert.True(viewModel.IsFailed);
        // The web player's sentence around the player's own reason.
        Assert.Equal("Playback error: loading failed", viewModel.ErrorMessage);
    }

    [Fact]
    public void Transport_DrivesTheEngine()
    {
        var viewModel = CreateViewModel();
        _engine.RaiseState(Loaded());

        viewModel.TogglePauseCommand.Execute(null);
        viewModel.SeekTo(20);
        viewModel.SeekTo(-3);
        viewModel.SeekTo(99);

        Assert.True(_engine.Current.IsPaused);
        Assert.Equal([TimeSpan.FromSeconds(20), TimeSpan.Zero, TimeSpan.FromSeconds(30)], _engine.Seeks);
    }

    [Fact]
    public void Pause_WaitsForSomethingToPlay()
    {
        // A pause set while preparing would hold the coming load at its first frame.
        var viewModel = CreateViewModel();

        viewModel.TogglePauseCommand.Execute(null);

        Assert.False(_engine.Current.IsPaused);
    }

    [Fact]
    public void ATrailer_PausesWhatPlays()
    {
        var viewModel = CreateViewModel();
        _engine.RaiseState(Loaded());

        Assert.True(viewModel.PauseForTrailer());

        Assert.True(_engine.Current.IsPaused);
    }

    [Fact]
    public void ATrailer_LeavesAPauseAsItIs()
    {
        var viewModel = CreateViewModel();
        _engine.RaiseState(Loaded() with { IsPaused = true });

        Assert.False(viewModel.PauseForTrailer());

        Assert.True(_engine.Current.IsPaused);
    }

    [Fact]
    public void ATrailer_DoesNotPauseALoadOnItsWay()
    {
        // As Pause: a pause set while preparing would hold the coming load at its first frame.
        var viewModel = CreateViewModel();

        Assert.False(viewModel.PauseForTrailer());

        Assert.False(_engine.Current.IsPaused);
    }

    [Fact]
    public void Skips_UseTheProfileIntervals_WithinTheTitle()
    {
        var viewModel = CreateViewModel();
        _engine.RaiseState(Loaded());

        viewModel.SkipBackCommand.Execute(null);
        viewModel.SkipForwardCommand.Execute(null);

        // 12.5 − 10 and 12.5 + 30 (capped at the 30 s duration).
        Assert.Equal([TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(30)], _engine.Seeks);
        Assert.Equal(10, viewModel.SkipBackSeconds);
        Assert.Equal(30, viewModel.SkipForwardSeconds);
    }

    [Fact]
    public void Volume_IsSetOnTheEngine_AndKeptInRange()
    {
        var viewModel = CreateViewModel();

        viewModel.Volume = 60;
        Assert.Equal(60, _engine.Current.Volume);

        viewModel.ChangeVolume(+50);
        Assert.Equal(100, _engine.Current.Volume);

        viewModel.ToggleMuteCommand.Execute(null);
        Assert.True(_engine.Current.IsMuted);
    }

    [Fact]
    public void ChangingTheVolume_Unmutes()
    {
        var viewModel = CreateViewModel();
        _engine.RaiseState(Loaded() with { IsMuted = true, Volume = 40 });

        viewModel.ChangeVolume(+5);

        Assert.False(_engine.Current.IsMuted);
        Assert.Equal(45, _engine.Current.Volume);
    }

    [Fact]
    public void AVolumeTheEngineReported_IsNotTheViewersChange()
    {
        var viewModel = CreateViewModel();
        // The engine has set the viewer's volume on a new mpv already.
        _engine.Current = Loaded() with { Volume = 40, IsMuted = true, Sequence = 5 };

        // That mpv's first notice, of its own volume, reaches the UI after that.
        _engine.RaiseState(Loaded() with { Volume = 100, IsMuted = true, Sequence = 2 });

        Assert.Equal(100, viewModel.Volume);
        Assert.Equal(40, _engine.Current.Volume);
        Assert.True(_engine.Current.IsMuted);
    }

    [Fact]
    public async Task Close_StopsAndLeavesThePlayer()
    {
        _navigation.NavigateTo(Granary.Core.Abstractions.AppRoute.Item, "movie-1");
        _navigation.NavigateTo(Granary.Core.Abstractions.AppRoute.Player);
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Direct(@"C:\Videos\movie.mkv"));

        viewModel.CloseCommand.Execute(null);

        Assert.Equal(1, _engine.Stops);
        Assert.Equal(1, _navigation.PlayerLeaves);
    }

    [Fact]
    public void Dispose_UnsubscribesFromTheEngine()
    {
        var viewModel = CreateViewModel();

        viewModel.Dispose();

        Assert.False(_engine.HasSubscribers);
    }

    private static PlaybackSnapshot Loaded() =>
        new(true, false, TimeSpan.FromSeconds(12.5), TimeSpan.FromSeconds(30), "d3d11va", HasStarted: true);
}
