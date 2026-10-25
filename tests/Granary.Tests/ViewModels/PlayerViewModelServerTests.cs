using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>The player page with a server title: the view model shows what the server playback reports.</summary>
public sealed class PlayerViewModelServerTests
{
    private readonly FakePlaybackEngine _engine = new();
    private readonly RecordingNavigation _navigation = new();
    private readonly List<FakeServerPlayback> _playbacks = [];

    private FakeServerPlayback Playback => _playbacks[^1];

    private PlayerViewModel CreateViewModel() => new(_engine, new ImmediateDispatcher(), _navigation, () =>
    {
        var playback = new FakeServerPlayback();
        _playbacks.Add(playback);
        return playback;
    });

    [Fact]
    public async Task Start_HandsTheRequestToAServerPlayback()
    {
        var viewModel = CreateViewModel();

        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));

        Assert.Equal(PlayRequest.Resume("movie-1"), Playback.Started);
        Assert.Empty(_engine.Loaded);
        Assert.True(viewModel.IsPreparing);
    }

    [Fact]
    public async Task TitleAndPhase_FollowThePlayback()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Resume("episode-1"));
        Playback.Title = "Lupin";
        Playback.Subtitle = "S03E01 · Chapter 1";
        Playback.Preferences = PlaybackPreferences.Default with { SkipBackSeconds = 15, SkipForwardSeconds = 45 };

        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));

        Assert.Equal("Lupin", viewModel.Title);
        Assert.Equal("S03E01 · Chapter 1", viewModel.Subtitle);
        Assert.True(viewModel.IsPlaying);
        Assert.Equal(15, viewModel.SkipBackSeconds);
        Assert.Equal(45, viewModel.SkipForwardSeconds);
    }

    [Fact]
    public async Task Positions_AreInTheTitlesOwnTime()
    {
        // A copy remux starts its stream at the keyframe before the resume point (docs/api-contract.md, 8.6).
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        Playback.TimelineOffsetSeconds = 120;
        Playback.DurationSeconds = 5400;
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));

        _engine.RaiseState(new PlaybackSnapshot(true, false, TimeSpan.FromSeconds(12.5), TimeSpan.FromSeconds(600), null, HasStarted: true));

        Assert.Equal(132.5, viewModel.PositionSeconds);
        Assert.Equal(5400, viewModel.DurationSeconds);
        Assert.True(viewModel.CanSeek);
    }

    [Fact]
    public async Task WithoutAKnownLength_TheStreamsLengthIsShiftedIntoTheTitlesTime()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        Playback.TimelineOffsetSeconds = 120;
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));

        _engine.RaiseState(new PlaybackSnapshot(true, false, TimeSpan.FromSeconds(12.5), TimeSpan.FromSeconds(600), null, HasStarted: true));

        Assert.Equal(720, viewModel.DurationSeconds);
    }

    [Fact]
    public async Task Seeks_GoThroughThePlayback()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        Playback.DurationSeconds = 5400;
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        _engine.RaiseState(new PlaybackSnapshot(true, false, TimeSpan.FromSeconds(100), null, null, HasStarted: true));

        viewModel.SeekTo(600);
        viewModel.SkipForwardCommand.Execute(null);

        // The skip goes on from the seek's target, which the engine has not reported yet.
        Assert.Equal([600, 630], Playback.Seeks);
        Assert.Empty(_engine.Seeks);
    }

    [Fact]
    public async Task Reconnecting_ShowsAsWaiting()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));

        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Reconnecting));

        Assert.True(viewModel.IsPlaying);
        Assert.True(viewModel.IsBuffering);
    }

    [Fact]
    public async Task AFailure_ShowsTheServersMessage_AndRetryStartsAgain()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));

        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Failed, "This title cannot be played here.", CanRetry: true));

        Assert.True(viewModel.IsFailed);
        Assert.Equal("This title cannot be played here.", viewModel.ErrorMessage);
        Assert.True(viewModel.RetryCommand.CanExecute(null));

        var failed = Playback;
        await viewModel.RetryCommand.ExecuteAsync(null);

        Assert.Equal(2, _playbacks.Count);
        Assert.Equal(1, failed.Stops);
        Assert.True(failed.Disposed);
        Assert.Equal(PlayRequest.Resume("movie-1"), Playback.Started);
        Assert.True(viewModel.IsPreparing);
    }

    [Fact]
    public async Task TheEnd_IsEnded()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));

        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Ended));

        Assert.True(viewModel.IsEnded);
    }

    [Fact]
    public async Task Close_StopsTheServerSession_AndGoesBack()
    {
        _navigation.NavigateTo(AppRoute.Item, "movie-1");
        _navigation.NavigateTo(AppRoute.Player);
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));

        viewModel.CloseCommand.Execute(null);

        Assert.Equal(1, Playback.Stops);
        Assert.Equal(1, _engine.Stops);
        Assert.Equal(1, _navigation.BackCount);
    }

    [Fact]
    public async Task Leave_StopsTheServerSessionAndTheEngine_Once()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));

        viewModel.Leave();
        viewModel.Leave();
        viewModel.Dispose();

        Assert.Equal(1, Playback.Stops);
        Assert.Equal(1, _engine.Stops);
        Assert.Equal(0, _navigation.BackCount);
    }

    [Fact]
    public async Task QuickSkips_AddUp()
    {
        // The engine's position lags behind a seek; a second press goes on from the first one's target.
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        Playback.DurationSeconds = 5400;
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        _engine.RaiseState(new PlaybackSnapshot(true, false, TimeSpan.FromSeconds(100), null, null, HasStarted: true));

        viewModel.SkipForwardCommand.Execute(null);
        viewModel.SkipForwardCommand.Execute(null);
        _engine.RaiseState(new PlaybackSnapshot(true, false, TimeSpan.FromSeconds(160), null, null, HasStarted: true));
        viewModel.SkipBackCommand.Execute(null);

        Assert.Equal([130, 160, 150], Playback.Seeks);
    }

    [Fact]
    public async Task Dispose_StopsTheServerSession()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));

        viewModel.Dispose();

        Assert.Equal(1, Playback.Stops);
        Assert.False(_engine.HasSubscribers);
    }
}
