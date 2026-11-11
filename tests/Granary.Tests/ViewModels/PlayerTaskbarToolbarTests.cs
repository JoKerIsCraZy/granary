using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Player;
using Granary.Tests.Fakes;
using static Granary.Core.Abstractions.TaskbarButtonState;

namespace Granary.Tests.ViewModels;

/// <summary>
/// Previous episode, Play/Pause and Next episode in the taskbar preview while the player has a title: what they show, and
/// a press as the player's own button.
/// </summary>
public sealed class PlayerTaskbarToolbarTests
{
    private const double Length = 2100;
    private static readonly PlayingEpisode Secrets = new("episode-1-3", "series-dark", "Dark", 1, 3);
    private static readonly PlayingEpisode DoubleLivesPlaying = new("episode-1-4", "series-dark", "Dark", 1, 4);
    private static readonly NextEpisode Lies = new("episode-1-2", 1, 2, "Lies");
    private static readonly NextEpisode DoubleLives = new("episode-1-4", 1, 4, "Double Lives");
    private static readonly NextEpisode SecretsBefore = new("episode-1-3", 1, 3, "Secrets");

    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly EpisodesAround _episodes = new();
    private readonly List<FakeServerPlayback> _playbacks = [];
    private readonly FakeTaskbarToolbar _toolbar = new();

    private FakeServerPlayback Playback => _playbacks[^1];

    private PlayerViewModel NewPlayer(PlayingEpisode? episode = null) =>
        new(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () =>
        {
            var playback = new FakeServerPlayback { DurationSeconds = Length };
            Plays(playback, episode);
            _playbacks.Add(playback);
            return playback;
        }, new PlaybackStops(), _time, _episodes);

    // What the server says the title is once its details have come: an episode, or (null) a movie.
    private static void Plays(FakeServerPlayback playback, PlayingEpisode? episode)
    {
        playback.Episode = episode;
        playback.MediaInfo = episode is null
            ? new PlaybackMediaInfo("Heat", "1995", null) { Kind = PlaybackMediaKind.Movie }
            : new PlaybackMediaInfo(episode.ContentId, "Dark", null) { Kind = PlaybackMediaKind.Episode };
    }

    private async Task<(PlayerViewModel Player, PlayerTaskbarToolbar Toolbar)> PlayingAsync(PlayingEpisode? episode = null,
        IUiDispatcher? dispatcher = null)
    {
        var player = NewPlayer(episode);
        var toolbar = new PlayerTaskbarToolbar(player, _toolbar, dispatcher ?? new ImmediateDispatcher());
        await player.StartAsync(PlayRequest.Resume(episode?.ContentId ?? "movie-heat"));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        At(30);
        return (player, toolbar);
    }

    private void At(double seconds, bool paused = false) =>
        _engine.RaiseState(new PlaybackSnapshot(true, paused, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(Length), null, HasStarted: true));

    [Fact]
    public async Task AMovie_ShowsOnlyPlayPause_PauseWhileItPlays_AndPlayWhilePaused()
    {
        await PlayingAsync();
        Assert.Equal(new TaskbarToolbarState(Hidden, Enabled, Hidden, IsPaused: false), _toolbar.Current);

        At(30, paused: true);

        Assert.Equal(new TaskbarToolbarState(Hidden, Enabled, Hidden, IsPaused: true), _toolbar.Current);
    }

    [Fact]
    public async Task ALocalFile_ShowsOnlyPlayPause()
    {
        var player = NewPlayer();
        using var toolbar = new PlayerTaskbarToolbar(player, _toolbar, new ImmediateDispatcher());

        await player.StartAsync(PlayRequest.Direct(@"C:\Videos\Holiday.mkv"));
        At(5);

        Assert.Equal(new TaskbarToolbarState(Hidden, Enabled, Hidden, IsPaused: false), _toolbar.Current);
    }

    [Fact]
    public async Task AnEpisodeWithEpisodesAround_ShowsAllThree()
    {
        _episodes.Previous = Lies;
        _episodes.Next = DoubleLives;

        await PlayingAsync(Secrets);

        Assert.Equal(new TaskbarToolbarState(Enabled, Enabled, Enabled, IsPaused: false), _toolbar.Current);
    }

    [Fact]
    public async Task ASeasonsFirstEpisode_HasPreviousGreyed()
    {
        _episodes.Next = DoubleLives;

        await PlayingAsync(Secrets);

        Assert.Equal(new TaskbarToolbarState(Disabled, Enabled, Enabled, IsPaused: false), _toolbar.Current);
    }

    [Fact]
    public async Task TheLastEpisode_HasNextGreyed()
    {
        _episodes.Previous = Lies;

        await PlayingAsync(Secrets);

        Assert.Equal(new TaskbarToolbarState(Enabled, Enabled, Disabled, IsPaused: false), _toolbar.Current);
    }

    [Theory]
    [InlineData(TaskbarButton.Previous, "episode-1-2")]
    [InlineData(TaskbarButton.Next, "episode-1-4")]
    public async Task PreviousAndNext_StartTheEpisodesAround_AsThePlayersOwnButtons(TaskbarButton button, string started)
    {
        _episodes.Previous = Lies;
        _episodes.Next = DoubleLives;
        await PlayingAsync(Secrets);

        _toolbar.Press(button);

        Assert.Equal(2, _playbacks.Count);
        Assert.Equal(PlayRequest.ResumeWithoutRewind(started), Playback.Started);
    }

    [Fact]
    public async Task AGreyedPreviousOrNext_DoesNothing()
    {
        _episodes.Next = DoubleLives;
        await PlayingAsync(Secrets);

        _toolbar.Press(TaskbarButton.Previous);
        Assert.Single(_playbacks);

        // Buffering: one press, one load.
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Reconnecting));
        Assert.Equal(new TaskbarToolbarState(Disabled, Enabled, Disabled, IsPaused: false), _toolbar.Current);
        _toolbar.Press(TaskbarButton.Next);
        Assert.Single(_playbacks);
    }

    [Fact]
    public async Task APressFromBeforeTheButtonGreyed_DoesNothing()
    {
        _episodes.Next = DoubleLives;
        var dispatcher = new QueuedDispatcher();
        await PlayingAsync(Secrets, dispatcher);
        dispatcher.RunAll();

        _toolbar.Press(TaskbarButton.Next);
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Reconnecting));
        dispatcher.RunAll();

        Assert.Single(_playbacks);
    }

    [Fact]
    public async Task WhileATitleLoads_PlayPauseShowsButIsOff_AndAPressDoesNothing()
    {
        var player = NewPlayer();
        using var toolbar = new PlayerTaskbarToolbar(player, _toolbar, new ImmediateDispatcher());

        await player.StartAsync(PlayRequest.Resume("movie-heat"));

        Assert.Equal(new TaskbarToolbarState(Hidden, Disabled, Hidden, IsPaused: false), _toolbar.Current);
        _toolbar.Press(TaskbarButton.PlayPause);
        Assert.False(_engine.Current.IsPaused);
    }

    [Fact]
    public async Task AFailedEpisode_KeepsAllThreeOff_UntilThePlayerIsLeft()
    {
        _episodes.Previous = Lies;
        _episodes.Next = DoubleLives;
        await PlayingAsync(Secrets);

        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Failed, "The server stopped answering.", CanRetry: true));

        Assert.Equal(new TaskbarToolbarState(Disabled, Disabled, Disabled, IsPaused: false), _toolbar.Current);
        _toolbar.Press(TaskbarButton.PlayPause);
        _toolbar.Press(TaskbarButton.Previous);
        _toolbar.Press(TaskbarButton.Next);
        Assert.False(_engine.Current.IsPaused);
        Assert.Single(_playbacks);
    }

    [Fact]
    public async Task ANewTitle_UpdatesThem_GreyedWhileItLoads()
    {
        _episodes.Next = DoubleLives;
        var (player, _) = await PlayingAsync(Secrets);

        _toolbar.Press(TaskbarButton.Next);

        // Loading the next episode: still an episode's buttons, all off.
        Assert.Equal(new TaskbarToolbarState(Disabled, Disabled, Disabled, IsPaused: false), _toolbar.Current);

        // It is the season's last.
        _episodes.Previous = SecretsBefore;
        _episodes.Next = null;
        Plays(Playback, DoubleLivesPlaying);
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        At(0);
        Assert.Equal(new TaskbarToolbarState(Enabled, Enabled, Disabled, IsPaused: false), _toolbar.Current);

        // A movie after it: Previous and Next go once it is known to be one.
        await player.StartAsync(PlayRequest.Resume("movie-heat"));
        Assert.Equal(new TaskbarToolbarState(Disabled, Disabled, Disabled, IsPaused: false), _toolbar.Current);
        Plays(Playback, null);
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        At(0);
        Assert.Equal(new TaskbarToolbarState(Hidden, Enabled, Hidden, IsPaused: false), _toolbar.Current);
    }

    [Fact]
    public async Task AnEpisodesFirstLoad_ShowsPlayPauseAlone_UntilItsDetailsCome()
    {
        _episodes.Next = DoubleLives;
        var player = NewPlayer(Secrets);
        using var toolbar = new PlayerTaskbarToolbar(player, _toolbar, new ImmediateDispatcher());

        await player.StartAsync(PlayRequest.Resume(Secrets.ContentId));
        Assert.Equal(new TaskbarToolbarState(Hidden, Disabled, Hidden, IsPaused: false), _toolbar.Current);

        // Its details come while it still loads.
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Preparing));
        Assert.Equal(new TaskbarToolbarState(Disabled, Disabled, Disabled, IsPaused: false), _toolbar.Current);

        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        At(0);
        Assert.Equal(new TaskbarToolbarState(Disabled, Enabled, Enabled, IsPaused: false), _toolbar.Current);
    }

    [Fact]
    public async Task ATitleThatFailsBeforeItsDetailsCome_AfterAnEpisode_HasNoPreviousOrNext_AlsoWhenTriedAgain()
    {
        _episodes.Next = DoubleLives;
        var (player, _) = await PlayingAsync(Secrets);

        await player.StartAsync(PlayRequest.Resume("movie-heat"));
        Playback.MediaInfo = PlaybackMediaInfo.None;
        Playback.Episode = null;
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Failed, "The server stopped answering.", CanRetry: true));

        Assert.Equal(new TaskbarToolbarState(Hidden, Disabled, Hidden, IsPaused: false), _toolbar.Current);

        player.RetryCommand.Execute(null);

        Assert.Equal(new TaskbarToolbarState(Hidden, Disabled, Hidden, IsPaused: false), _toolbar.Current);
    }

    [Fact]
    public async Task APlayerStartedAgainAfterLeaving_NeverShowsTheTitleBefore()
    {
        _episodes.Previous = Lies;
        _episodes.Next = DoubleLives;
        var (player, _) = await PlayingAsync(Secrets);
        player.Leave();
        int shown = _toolbar.Shown.Count;

        await player.StartAsync(PlayRequest.Resume("movie-heat"));

        Assert.NotEmpty(_toolbar.Shown.Skip(shown));
        Assert.All(_toolbar.Shown.Skip(shown), state => Assert.Equal(new TaskbarToolbarState(Hidden, Disabled, Hidden, IsPaused: false), state));
    }

    [Fact]
    public async Task APlayPausePress_PlaysOrPausesAsThePlayersOwnButton()
    {
        await PlayingAsync();

        _toolbar.Press(TaskbarButton.PlayPause);
        Assert.True(_engine.Current.IsPaused);
        At(30, paused: true);

        _toolbar.Press(TaskbarButton.PlayPause);
        Assert.False(_engine.Current.IsPaused);
    }

    [Fact]
    public async Task APress_IsHandledOnTheUiThread_AfterTheWindowsMessage()
    {
        var dispatcher = new QueuedDispatcher();
        await PlayingAsync(dispatcher: dispatcher);
        dispatcher.RunAll();

        _toolbar.Press(TaskbarButton.PlayPause);
        Assert.False(_engine.Current.IsPaused);

        dispatcher.RunAll();
        Assert.True(_engine.Current.IsPaused);
    }

    [Fact]
    public async Task APlayerLeft_HidesAllThreeAtOnce_AndAPressDoesNothing()
    {
        _episodes.Previous = Lies;
        _episodes.Next = DoubleLives;
        var (player, toolbar) = await PlayingAsync(Secrets);

        player.Leave();

        Assert.Equal(1, _toolbar.Hides);
        Assert.Null(_toolbar.Current);
        _toolbar.Press(TaskbarButton.PlayPause);
        _toolbar.Press(TaskbarButton.Previous);
        _toolbar.Press(TaskbarButton.Next);
        Assert.False(_engine.Current.IsPaused);
        Assert.Single(_playbacks);

        // The page goes after: hidden already, the buttons are not hidden twice.
        toolbar.Dispose();
        Assert.Equal(1, _toolbar.Hides);
    }

    [Fact]
    public async Task APlayerStartedAgainAfterLeaving_ShowsTheButtonsAgain()
    {
        var (player, _) = await PlayingAsync();
        player.Leave();

        await player.StartAsync(PlayRequest.Resume("movie-heat"));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        At(5);

        Assert.Equal(new TaskbarToolbarState(Hidden, Enabled, Hidden, IsPaused: false), _toolbar.Current);
    }

    [Fact]
    public async Task ThePageGoing_HidesTheButtons_AndAPressAfterDoesNothing()
    {
        _episodes.Next = DoubleLives;
        var (_, toolbar) = await PlayingAsync(Secrets);

        toolbar.Dispose();

        Assert.Equal(1, _toolbar.Hides);
        Assert.Null(_toolbar.Current);
        _toolbar.Press(TaskbarButton.PlayPause);
        _toolbar.Press(TaskbarButton.Next);
        Assert.False(_engine.Current.IsPaused);
        Assert.Single(_playbacks);
    }

    [Fact]
    public async Task PositionTicks_ChangeNothingShown_SoTheButtonsAreLeftAlone()
    {
        _episodes.Previous = Lies;
        _episodes.Next = DoubleLives;
        await PlayingAsync(Secrets);
        int shown = _toolbar.Shown.Count;

        for (double at = 30.25; at < 40; at += 0.25)
        {
            _time.Advance(TimeSpan.FromSeconds(0.25));
            At(at);
        }

        Assert.Equal(shown, _toolbar.Shown.Count);
    }

    [Fact]
    public async Task EachChange_ShowsOnce()
    {
        await PlayingAsync();
        int shown = _toolbar.Shown.Count;

        At(30, paused: true);
        At(30.25, paused: true);
        At(30.25, paused: false);

        Assert.Equal(shown + 2, _toolbar.Shown.Count);
    }

    /// <summary>The episodes around the one that plays, as the test sets them.</summary>
    private sealed class EpisodesAround : IUpNextSource
    {
        public NextEpisode? Previous { get; set; }

        public NextEpisode? Next { get; set; }

        public Task<NextEpisode?> NextEpisodeAsync(PlayingEpisode current, CancellationToken cancellationToken) => Task.FromResult(Next);

        public Task<NextEpisode?> PreviousEpisodeAsync(PlayingEpisode current, CancellationToken cancellationToken) => Task.FromResult(Previous);

        public Task<IReadOnlyList<MediaCard>> OnDeckAsync(string? exceptSeriesId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaCard>>([]);
    }
}
