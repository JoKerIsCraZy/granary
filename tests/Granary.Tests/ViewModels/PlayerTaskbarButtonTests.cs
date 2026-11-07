using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Player;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>The Play/Pause in the taskbar preview while the player has a title: what it shows, and a press as the player's own.</summary>
public sealed class PlayerTaskbarButtonTests
{
    private const double Length = 2100;

    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<FakeServerPlayback> _playbacks = [];
    private readonly FakeTaskbarPlayButton _button = new();

    private FakeServerPlayback Playback => _playbacks[^1];

    private PlayerViewModel NewPlayer() =>
        new(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () =>
        {
            var playback = new FakeServerPlayback { DurationSeconds = Length };
            _playbacks.Add(playback);
            return playback;
        }, new PlaybackStops(), _time);

    private async Task<(PlayerViewModel Player, PlayerTaskbarButton Button)> PlayingAsync(IUiDispatcher? dispatcher = null)
    {
        var player = NewPlayer();
        var button = new PlayerTaskbarButton(player, _button, dispatcher ?? new ImmediateDispatcher());
        await player.StartAsync(PlayRequest.Resume("movie-heat"));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        At(30);
        return (player, button);
    }

    private void At(double seconds, bool paused = false) =>
        _engine.RaiseState(new PlaybackSnapshot(true, paused, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(Length), null, HasStarted: true));

    [Fact]
    public async Task WhileATitlePlays_ThePreviewShowsPause_AndWhilePausedPlay()
    {
        await PlayingAsync();
        Assert.Equal(new TaskbarPlayButtonState(IsPaused: false, IsEnabled: true), _button.Current);

        At(30, paused: true);

        Assert.Equal(new TaskbarPlayButtonState(IsPaused: true, IsEnabled: true), _button.Current);
    }

    [Fact]
    public async Task WhileATitleLoads_TheButtonShowsButIsOff_AndAPressDoesNothing()
    {
        var player = NewPlayer();
        using var button = new PlayerTaskbarButton(player, _button, new ImmediateDispatcher());

        await player.StartAsync(PlayRequest.Resume("movie-heat"));

        Assert.Equal(new TaskbarPlayButtonState(IsPaused: false, IsEnabled: false), _button.Current);
        _button.Press();
        Assert.False(_engine.Current.IsPaused);
    }

    [Fact]
    public async Task AFailedTitle_KeepsTheButtonOff_UntilThePlayerIsLeft()
    {
        await PlayingAsync();

        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Failed, "The server stopped answering.", CanRetry: true));

        Assert.False(_button.Current!.IsEnabled);
        _button.Press();
        Assert.False(_engine.Current.IsPaused);
    }

    [Fact]
    public async Task APress_PlaysOrPausesAsThePlayersOwnButton()
    {
        await PlayingAsync();

        _button.Press();
        Assert.True(_engine.Current.IsPaused);
        At(30, paused: true);

        _button.Press();
        Assert.False(_engine.Current.IsPaused);
    }

    [Fact]
    public async Task APress_IsHandledOnTheUiThread_AfterTheWindowsMessage()
    {
        var dispatcher = new QueuedDispatcher();
        await PlayingAsync(dispatcher);
        dispatcher.RunAll();

        _button.Press();
        Assert.False(_engine.Current.IsPaused);

        dispatcher.RunAll();
        Assert.True(_engine.Current.IsPaused);
    }

    [Fact]
    public async Task APlayerLeft_HidesTheButtonAtOnce_AndAPressDoesNothing()
    {
        var (player, button) = await PlayingAsync();

        player.Leave();

        Assert.Equal(1, _button.Hides);
        Assert.Null(_button.Current);
        _button.Press();
        Assert.False(_engine.Current.IsPaused);

        // The page goes after: hidden already, the button is not hidden twice.
        button.Dispose();
        Assert.Equal(1, _button.Hides);
    }

    [Fact]
    public async Task APlayerStartedAgainAfterLeaving_ShowsTheButtonAgain()
    {
        var (player, _) = await PlayingAsync();
        player.Leave();

        await player.StartAsync(PlayRequest.Resume("movie-heat"));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        At(5);

        Assert.Equal(new TaskbarPlayButtonState(IsPaused: false, IsEnabled: true), _button.Current);
    }

    [Fact]
    public async Task ThePageGoing_HidesTheButton_AndAPressAfterDoesNothing()
    {
        var (_, button) = await PlayingAsync();

        button.Dispose();

        Assert.Equal(1, _button.Hides);
        Assert.Null(_button.Current);
        _button.Press();
        Assert.False(_engine.Current.IsPaused);
    }

    [Fact]
    public async Task PositionTicks_ChangeNothingShown_SoTheButtonIsLeftAlone()
    {
        await PlayingAsync();
        int shown = _button.Shown.Count;

        for (double at = 30.25; at < 40; at += 0.25)
        {
            _time.Advance(TimeSpan.FromSeconds(0.25));
            At(at);
        }

        Assert.Equal(shown, _button.Shown.Count);
    }

    [Fact]
    public async Task EachChange_ShowsOnce()
    {
        await PlayingAsync();
        int shown = _button.Shown.Count;

        At(30, paused: true);
        At(30.25, paused: true);
        At(30.25, paused: false);

        Assert.Equal(shown + 2, _button.Shown.Count);
    }
}
