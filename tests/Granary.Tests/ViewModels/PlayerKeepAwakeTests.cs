using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Player;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>What the player keeps awake: the display while a title loads, plays or buffers, nothing while paused or after it.</summary>
public sealed class PlayerKeepAwakeTests
{
    private const double Length = 2100;

    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<FakeServerPlayback> _playbacks = [];
    private readonly FakeKeepAwake _awake = new();

    private FakeServerPlayback Playback => _playbacks[^1];

    private PlayerViewModel NewPlayer() =>
        new(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () =>
        {
            var playback = new FakeServerPlayback { DurationSeconds = Length };
            _playbacks.Add(playback);
            return playback;
        }, new PlaybackStops(), _time);

    private async Task<(PlayerViewModel Player, PlayerKeepAwake KeepAwake)> PlayingAsync()
    {
        var player = NewPlayer();
        var keepAwake = new PlayerKeepAwake(player, _awake);
        await player.StartAsync(PlayRequest.Resume("movie-heat"));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        At(30);
        return (player, keepAwake);
    }

    private void At(double seconds, bool paused = false, bool buffering = false, bool audioOnly = false) =>
        _engine.RaiseState(new PlaybackSnapshot(true, paused, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(Length), null,
            HasStarted: true, IsBuffering: buffering, IsAudioOnly: audioOnly));

    [Fact]
    public async Task ATitleThatLoads_KeepsTheDisplayOn_AsItIsAboutToPlay()
    {
        var player = NewPlayer();
        using var keepAwake = new PlayerKeepAwake(player, _awake);

        await player.StartAsync(PlayRequest.Resume("movie-heat"));

        Assert.Equal([KeepAwakeState.Display], _awake.Requests);
    }

    [Fact]
    public async Task APlayingTitle_KeepsTheDisplayOn_AskedOnceFromItsLoad()
    {
        await PlayingAsync();

        Assert.Equal([KeepAwakeState.Display], _awake.Requests);
    }

    [Fact]
    public async Task Buffering_KeepsTheDisplayOn()
    {
        await PlayingAsync();

        At(30, buffering: true);
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Reconnecting));

        Assert.Equal([KeepAwakeState.Display], _awake.Requests);
    }

    [Fact]
    public async Task APause_LetsTheDisplaySleep_AsAndroidsPlayerDoes_AndPlayingOnHoldsItAgain()
    {
        await PlayingAsync();

        At(30, paused: true);
        Assert.Equal(KeepAwakeState.None, _awake.Current);

        At(30);
        Assert.Equal([KeepAwakeState.Display, KeepAwakeState.None, KeepAwakeState.Display], _awake.Requests);
    }

    [Fact]
    public async Task APauseWhileBuffering_LetsTheDisplaySleep()
    {
        await PlayingAsync();

        At(30, paused: true, buffering: true);

        Assert.Equal(KeepAwakeState.None, _awake.Current);
    }

    [Fact]
    public async Task TheEnd_LetsGo()
    {
        await PlayingAsync();

        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Ended));

        Assert.Equal([KeepAwakeState.Display, KeepAwakeState.None], _awake.Requests);
    }

    [Fact]
    public async Task AFailure_LetsGo()
    {
        await PlayingAsync();

        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Failed, "The server stopped answering.", CanRetry: true));

        Assert.Equal([KeepAwakeState.Display, KeepAwakeState.None], _awake.Requests);
    }

    [Fact]
    public async Task LeavingThePlayer_LetsGoAtOnce_AndDisposingAfterAsksNothingMore()
    {
        var (player, keepAwake) = await PlayingAsync();

        player.Leave();
        Assert.Equal([KeepAwakeState.Display, KeepAwakeState.None], _awake.Requests);

        keepAwake.Dispose();
        Assert.Equal([KeepAwakeState.Display, KeepAwakeState.None], _awake.Requests);
    }

    [Fact]
    public async Task Disposing_Releases_AndThePlayersChangesAfterAskNothing()
    {
        var (_, keepAwake) = await PlayingAsync();

        keepAwake.Dispose();
        At(31, paused: true);
        At(31);

        Assert.Equal([KeepAwakeState.Display, KeepAwakeState.None], _awake.Requests);
    }

    [Fact]
    public async Task ANewTitle_HoldsTheDisplayAgain()
    {
        var (player, _) = await PlayingAsync();
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Ended));

        await player.StartAsync(PlayRequest.Resume("episode-2"));

        Assert.Equal(KeepAwakeState.Display, _awake.Current);
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        At(5);
        Assert.Equal([KeepAwakeState.Display, KeepAwakeState.None, KeepAwakeState.Display], _awake.Requests);
    }

    [Fact]
    public async Task ANextEpisodeThatFails_LetsGo()
    {
        var (player, _) = await PlayingAsync();
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Ended));
        await player.StartAsync(PlayRequest.Resume("episode-2"));

        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Failed, "The server stopped answering.", CanRetry: true));

        Assert.Equal([KeepAwakeState.Display, KeepAwakeState.None, KeepAwakeState.Display, KeepAwakeState.None], _awake.Requests);
    }

    [Fact]
    public async Task ATitleStartedAgainAfterLeaving_HoldsTheDisplayAgain()
    {
        var (player, _) = await PlayingAsync();
        player.Leave();

        await player.StartAsync(PlayRequest.Resume("movie-heat"));

        Assert.Equal([KeepAwakeState.Display, KeepAwakeState.None, KeepAwakeState.Display], _awake.Requests);
    }

    [Fact]
    public async Task PositionTicks_AskNothing()
    {
        await PlayingAsync();

        for (double at = 30.25; at < 60; at += 0.25)
        {
            _time.Advance(TimeSpan.FromSeconds(0.25));
            At(at);
        }

        Assert.Equal([KeepAwakeState.Display], _awake.Requests);
    }

    // Only playback decides, so the bar and the mini view, which are the same player page, keep the hold (not tested here).
    [Fact]
    public async Task PlaybackThatGoesOn_KeepsTheDisplayOn_WithoutAskingAgain()
    {
        var (player, _) = await PlayingAsync();

        for (int minute = 1; minute <= 90; minute++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            At(30 + (minute * 60));
        }
        player.Volume = 40;
        player.ToggleMuteCommand.Execute(null);

        Assert.Equal([KeepAwakeState.Display], _awake.Requests);
    }

    [Fact]
    public async Task SoundWithoutAPicture_KeepsOnlyTheSystemAwake_AndAPauseLetsThatGoToo()
    {
        await PlayingAsync();

        At(30, audioOnly: true);
        Assert.Equal(KeepAwakeState.System, _awake.Current);

        At(30, paused: true, audioOnly: true);
        Assert.Equal([KeepAwakeState.Display, KeepAwakeState.System, KeepAwakeState.None], _awake.Requests);
    }

    [Fact]
    public async Task APictureAfterSound_HoldsTheDisplayAgain()
    {
        var player = NewPlayer();
        using var keepAwake = new PlayerKeepAwake(player, _awake);
        await player.StartAsync(PlayRequest.Direct(@"C:\Music\song.flac"));
        At(1, audioOnly: true);
        Assert.Equal(KeepAwakeState.System, _awake.Current);

        await player.StartAsync(PlayRequest.Direct(@"C:\Videos\clip.mkv"));
        At(1);

        Assert.Equal([KeepAwakeState.Display, KeepAwakeState.System, KeepAwakeState.Display], _awake.Requests);
    }

    [Fact]
    public async Task AHoldWindowsRefused_IsAskedAgain_WithThePlayersNextChange()
    {
        _awake.RefuseNext = 1;

        await PlayingAsync();

        // Refused as the title began to load; asked again as it began to play.
        Assert.Equal([KeepAwakeState.Display, KeepAwakeState.Display], _awake.Requests);
        Assert.Equal(KeepAwakeState.Display, _awake.Current);
    }

    // A release Windows refused part-way may have let the display go all the same: playing on asks for it again rather
    // than counting on what was held before.
    [Fact]
    public async Task AReleaseRefusedOnPause_PlayingOnAsksForTheDisplayAgain()
    {
        await PlayingAsync();
        _awake.RefuseNext = 1;
        At(30, paused: true);

        At(31, paused: false);

        Assert.Equal([KeepAwakeState.Display, KeepAwakeState.None, KeepAwakeState.Display], _awake.Requests);
        Assert.Equal(KeepAwakeState.Display, _awake.Current);
    }

    [Fact]
    public async Task AReleaseWindowsRefused_IsAskedAgain_WithThePlayersNextChange_NotWithAPositionTick()
    {
        await PlayingAsync();
        _awake.RefuseNext = 1;
        At(30, paused: true);
        At(30.5, paused: true);
        Assert.Equal([KeepAwakeState.Display, KeepAwakeState.None], _awake.Requests);

        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Ended));

        Assert.Equal([KeepAwakeState.Display, KeepAwakeState.None, KeepAwakeState.None], _awake.Requests);
        Assert.Equal(KeepAwakeState.None, _awake.Current);
    }

    [Fact]
    public async Task AFilePlayedDirectly_HoldsFromItsLoad_AndLetsGoAtItsEnd()
    {
        var player = NewPlayer();
        using var keepAwake = new PlayerKeepAwake(player, _awake);

        await player.StartAsync(PlayRequest.Direct(@"C:\Videos\clip.mkv"));
        At(1);
        Assert.Equal([KeepAwakeState.Display], _awake.Requests);

        _engine.RaiseEnded(new PlaybackEnded(PlaybackEndReason.EndOfFile, null));

        Assert.Equal([KeepAwakeState.Display, KeepAwakeState.None], _awake.Requests);
    }
}
