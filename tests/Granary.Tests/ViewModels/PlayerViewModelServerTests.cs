using Granary.Core.Abstractions;
using Granary.Core.Api.Playback;
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
    private readonly PlaybackStops _stops = new();

    private FakeServerPlayback Playback => _playbacks[^1];

    private PlayerViewModel CreateViewModel() => new(_engine, new ImmediateDispatcher(), _navigation, () =>
    {
        var playback = new FakeServerPlayback();
        _playbacks.Add(playback);
        return playback;
    }, _stops);

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
        Playback.Subtitle = "S3 · E1 — Chapter 1";
        Playback.Preferences = PlaybackPreferences.Default with { SkipBackSeconds = 15, SkipForwardSeconds = 45 };

        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));

        Assert.Equal("Lupin", viewModel.Title);
        Assert.Equal("S3 · E1 — Chapter 1", viewModel.Subtitle);
        Assert.True(viewModel.IsPlaying);
        Assert.Equal(15, viewModel.SkipBackSeconds);
        Assert.Equal(45, viewModel.SkipForwardSeconds);
    }

    [Fact]
    public async Task TheAudioMenu_FollowsThePlaybacksTracks()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        Assert.False(viewModel.HasAudioTracks);

        Playback.RaiseTracks([new WatchAudioTrack { Language = "eng" }], selected: 0);
        Assert.True(viewModel.HasAudioTracks);
        Assert.False(viewModel.CanChooseAudio);

        Playback.RaiseTracks([new WatchAudioTrack { Language = "eng" }, new WatchAudioTrack { Language = "ger" }], selected: 1);
        Assert.True(viewModel.CanChooseAudio);
        Assert.Equal([false, true], viewModel.AudioTracks.Select(row => row.IsSelected));
    }

    [Fact]
    public async Task TheSubtitleMenu_FollowsThePlaybacksInventory_AndPicksGoToIt()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        Playback.RaiseSubtitles([], selected: null);
        // Off alone: the menu still adds subtitles.
        Assert.True(viewModel.HasSubtitleMenu);
        Assert.Equal([true], viewModel.SubtitleTracks.Select(row => row.IsSelected));

        Playback.RaiseSubtitles([new SubtitleInventoryItem { TrackId = "file:1:subtitle:0", Source = "embedded", Language = "eng" }], "file:1:subtitle:0");
        viewModel.SelectSubtitle(null);

        Assert.True(viewModel.IsSubtitleOn);
        Assert.Equal([false, true], viewModel.SubtitleTracks.Select(row => row.IsSelected));
        Assert.Equal([null], Playback.SubtitlePicks);
    }

    [Theory]
    [InlineData(0, "0 ms", true)]
    [InlineData(300, "+300 ms", true)]
    [InlineData(-1200, "−1200 ms", true)]
    [InlineData(10_000, "+10000 ms", false)]
    public async Task TheSubtitleDelay_IsWrittenAsOnTheWeb(int delay, string text, bool canDelayLater)
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        Playback.RaiseSubtitles([new SubtitleInventoryItem { TrackId = "file:1:subtitle:0", Source = "embedded", Language = "eng" }], "file:1:subtitle:0");

        viewModel.ChangeSubtitleDelay(delay);

        Assert.Equal(delay, Playback.SubtitleDelayMs);
        Assert.Equal(text, viewModel.SubtitleDelayText);
        Assert.Equal(canDelayLater, viewModel.CanDelayLater);
        Assert.Equal(delay != 0, viewModel.CanResetSubtitleDelay);
    }

    [Fact]
    public async Task ResettingTheSubtitleDelay_TakesItBackToZero()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        viewModel.ChangeSubtitleDelay(-300);

        viewModel.ResetSubtitleDelay();

        Assert.Equal(0, Playback.SubtitleDelayMs);
        Assert.Equal("0 ms", viewModel.SubtitleDelayText);
    }

    [Fact]
    public async Task AnAudioPick_GoesToThePlayback()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));

        viewModel.SelectAudio(1);

        Assert.Equal([1], Playback.AudioPicks);
    }

    [Fact]
    public async Task Leaving_EmptiesTheAudioMenu_AndStopsListening()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        var playback = Playback;
        playback.RaiseTracks([new WatchAudioTrack(), new WatchAudioTrack()], selected: 0);

        viewModel.Leave();
        playback.RaiseTracks([new WatchAudioTrack(), new WatchAudioTrack()], selected: 1);

        Assert.Empty(viewModel.AudioTracks);
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
    public async Task Leave_LetsClosingTheAppWaitForTheStop()
    {
        var viewModel = CreateViewModel();
        await viewModel.StartAsync(PlayRequest.Resume("movie-1"));
        var stop = new TaskCompletionSource();
        Playback.StopResult = stop.Task;

        viewModel.Leave();
        var closing = _stops.WhenStoppedAsync(TimeSpan.FromSeconds(30));

        Assert.False(closing.IsCompleted);
        stop.SetResult();
        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
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
