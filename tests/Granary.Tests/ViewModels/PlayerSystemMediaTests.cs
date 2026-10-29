using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Player;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>Windows' media controls and keys while the player plays: what they show, and their buttons as the player's own.</summary>
public sealed class PlayerSystemMediaTests
{
    private const double Length = 2100;
    private static readonly Uri Poster = new("https://silo.example/api/v2/artwork/p.webp?sig=x");
    private static readonly PlayingEpisode Pilot = new("episode-1-3", "series-dark", "Dark", 1, 3);

    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly RecordingNavigation _navigation = new();
    private readonly EpisodesAround _episodes = new();
    private readonly List<FakeServerPlayback> _playbacks = [];
    private readonly FakeSystemMediaControls _controls = new();

    private FakeServerPlayback Playback => _playbacks[^1];

    private PlayerViewModel NewPlayer(IUiDispatcher? dispatcher = null, PlayingEpisode? episode = null) =>
        new(_engine, dispatcher ?? new ImmediateDispatcher(), _navigation, () =>
        {
            var playback = new FakeServerPlayback
            {
                DurationSeconds = Length,
                Episode = episode,
                MediaInfo = episode is null ? new PlaybackMediaInfo("Heat", "1995", Poster) : new PlaybackMediaInfo("Secrets", "Dark — S1 · E3", Poster),
                Preferences = PlaybackPreferences.Default with { SkipBackSeconds = 15, SkipForwardSeconds = 45 },
            };
            _playbacks.Add(playback);
            return playback;
        }, new PlaybackStops(), _time, _episodes);

    private async Task<(PlayerViewModel Player, PlayerSystemMedia Media)> PlayingAsync(PlayingEpisode? episode = null, IUiDispatcher? dispatcher = null)
    {
        var player = NewPlayer(dispatcher, episode);
        var media = new PlayerSystemMedia(player, _controls, dispatcher ?? new ImmediateDispatcher(), _time);
        await player.StartAsync(PlayRequest.Resume(episode?.ContentId ?? "movie-heat"));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        At(30);
        return (player, media);
    }

    private void At(double seconds, bool paused = false) =>
        _engine.RaiseState(new PlaybackSnapshot(true, paused, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(Length), null, HasStarted: true));

    [Fact]
    public async Task AMovie_ShowsItsLinesPictureAndTimeline_WithPlayPauseAndSeeking()
    {
        await PlayingAsync();

        Assert.Equal(new SystemMediaState("Heat", "1995", Poster, SystemMediaStatus.Playing, 30, Length,
            CanPlayPause: true, CanSeek: true, CanGoPrevious: false, CanGoNext: false), _controls.Current);
    }

    [Fact]
    public async Task ALocalFile_ShowsItsName_WithoutAPicture()
    {
        var player = NewPlayer();
        using var media = new PlayerSystemMedia(player, _controls, new ImmediateDispatcher(), _time);

        await player.StartAsync(PlayRequest.Direct(@"C:\Videos\Holiday.mkv"));
        At(5);

        Assert.Equal(("Holiday.mkv", "", null, SystemMediaStatus.Playing),
            (_controls.Current!.Title, _controls.Current.Subtitle, _controls.Current.Artwork, _controls.Current.Status));
    }

    [Fact]
    public async Task WhileATitleLoads_TheControlsSaySo_AndNoButtonWorks()
    {
        var player = NewPlayer();
        _ = new PlayerSystemMedia(player, _controls, new ImmediateDispatcher(), _time);

        await player.StartAsync(PlayRequest.Resume("movie-heat"));

        Assert.Equal(SystemMediaStatus.Changing, _controls.Current!.Status);
        Assert.False(_controls.Current.CanPlayPause || _controls.Current.CanSeek || _controls.Current.CanGoPrevious || _controls.Current.CanGoNext);
        _controls.Press(SystemMediaButton.Pause);
        Assert.False(_engine.Current.IsPaused);
    }

    [Fact]
    public async Task PlayAndPause_AreThePlayersOwn_AndAKeyForWhatIsSoAlreadyDoesNothing()
    {
        await PlayingAsync();

        _controls.Press(SystemMediaButton.Pause);
        Assert.True(_engine.Current.IsPaused);
        At(30, paused: true);
        Assert.Equal(SystemMediaStatus.Paused, _controls.Current!.Status);

        _controls.Press(SystemMediaButton.Pause);
        Assert.True(_engine.Current.IsPaused);

        _controls.Press(SystemMediaButton.Play);
        Assert.False(_engine.Current.IsPaused);
    }

    [Fact]
    public async Task RewindAndFastForward_SkipByTheProfilesSeconds_AndTheTimelineSeeks()
    {
        await PlayingAsync();

        _controls.Press(SystemMediaButton.FastForward);
        _controls.Press(SystemMediaButton.Rewind);
        _controls.Press(SystemMediaButton.Seek, 600);

        Assert.Equal([75, 60, 600], Playback.Seeks);
    }

    [Fact]
    public async Task AnEpisode_OffersTheEpisodesAround_AndNextStartsTheNextOne()
    {
        _episodes.Previous = new NextEpisode("episode-1-2", 1, 2, "Lies");
        _episodes.Next = new NextEpisode("episode-1-4", 1, 4, "Double Lives");
        await PlayingAsync(Pilot);

        Assert.Equal(("Secrets", "Dark — S1 · E3", true, true),
            (_controls.Current!.Title, _controls.Current.Subtitle, _controls.Current.CanGoPrevious, _controls.Current.CanGoNext));

        _controls.Press(SystemMediaButton.Next);

        Assert.Equal(PlayRequest.Resume("episode-1-4"), Playback.Started);
    }

    [Fact]
    public async Task PreviousStartsTheEpisodeBefore()
    {
        _episodes.Previous = new NextEpisode("episode-1-2", 1, 2, "Lies");
        await PlayingAsync(Pilot);

        _controls.Press(SystemMediaButton.Previous);

        Assert.Equal(PlayRequest.Resume("episode-1-2"), Playback.Started);
    }

    [Fact]
    public async Task ASeasonsFirstEpisode_HasNoPrevious_AndItsKeyDoesNothing()
    {
        _episodes.Next = new NextEpisode("episode-1-4", 1, 4, "Double Lives");
        await PlayingAsync(Pilot);

        Assert.False(_controls.Current!.CanGoPrevious);
        _controls.Press(SystemMediaButton.Previous);

        Assert.Single(_playbacks);
    }

    [Fact]
    public async Task TheTimeline_FollowsPlayback_AtMostEveryTwoSeconds_ButAJumpShowsAtOnce()
    {
        await PlayingAsync();
        int shown = _controls.Shown.Count;

        for (double at = 30.25; at < 31.5; at += 0.25)
        {
            _time.Advance(TimeSpan.FromSeconds(0.25));
            At(at);
        }
        Assert.Equal(shown, _controls.Shown.Count);

        _time.Advance(TimeSpan.FromSeconds(1));
        At(32.5);
        Assert.Equal((shown + 1, 32.5), (_controls.Shown.Count, _controls.Current!.PositionSeconds));

        _time.Advance(TimeSpan.FromSeconds(0.25));
        At(900);
        Assert.Equal((shown + 2, 900), (_controls.Shown.Count, _controls.Current.PositionSeconds));
    }

    [Fact]
    public async Task AnEndedTitle_IsStopped_AndClosingThePlayerTakesItOffTheControls()
    {
        var (_, media) = await PlayingAsync();

        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Ended));
        Assert.Equal((SystemMediaStatus.Stopped, false, false), (_controls.Current!.Status, _controls.Current.CanPlayPause, _controls.Current.CanSeek));

        media.Dispose();

        Assert.Equal(1, _controls.Clears);
        Assert.Null(_controls.Current);
        _controls.Press(SystemMediaButton.Seek, 100);
        Assert.Empty(Playback.Seeks);
    }

    [Fact]
    public async Task AButtonFromAnotherThread_IsHandledOnTheUiThread()
    {
        var dispatcher = new QueuedDispatcher();
        await PlayingAsync(dispatcher: dispatcher);
        dispatcher.RunAll();

        _controls.Press(SystemMediaButton.Pause);
        Assert.False(_engine.Current.IsPaused);

        dispatcher.RunAll();
        Assert.True(_engine.Current.IsPaused);
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
