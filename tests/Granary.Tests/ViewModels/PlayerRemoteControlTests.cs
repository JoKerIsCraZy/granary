using System.Text;
using Granary.Core.Abstractions;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Core.Remote;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Player;
using Granary.Tests.Fakes;
using Granary.Tests.Remote;

namespace Granary.Tests.ViewModels;

/// <summary>
/// The open player as Silo's phone remote sees it: the status a phone is shown, and a phone's commands as the player's own
/// buttons, every number from the phone checked first.
/// </summary>
public sealed class PlayerRemoteControlTests
{
    private const double Length = 6000;
    private const string MovieId = "movie-1";
    private const string EpisodeId = "episode-1-3";
    private const string NextEpisodeId = "episode-1-4";
    private static readonly PlayingEpisode Episode = new(EpisodeId, "series-1", "Example Series", 1, 3);
    private static readonly Uri Poster = new("https://silo.example/api/v2/artwork/p.webp?sig=x");

    private static readonly WatchAudioTrack[] Audio =
    [
        new() { Title = "English", Language = "en", Codec = "eac3", Channels = 6, Default = true },
        new() { Title = "Commentary", Language = "en", Codec = "aac", Channels = 2 },
    ];

    private static readonly SubtitleInventoryItem[] Subtitles =
    [
        new() { TrackId = "file:f1:subtitle:0", CombinedIndex = 0, Source = SubtitleSources.Embedded, Codec = "subrip", Language = "en" },
        new() { TrackId = "file:f1:subtitle:1", CombinedIndex = 1, Source = SubtitleSources.Embedded, Codec = "subrip", Language = "de" },
        new() { TrackId = "file:f1:subtitle:2", CombinedIndex = 2, Source = SubtitleSources.Embedded, Codec = "ass", Language = "fr" },
    ];

    private static readonly AvailableQuality[] Qualities =
    [
        new() { Label = "1080p", DisplayName = "1080p High", BitrateKbps = 8000 },
        new() { Label = "720p", BitrateKbps = 4000 },
    ];

    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly RecordingNavigation _navigation = new();
    private readonly EpisodesAround _episodes = new();
    private readonly List<FakeServerPlayback> _playbacks = [];
    // The receiver's own posts wait here, so it reads the player's status only when a test lets it.
    private readonly HeldDispatcher _receiverThread = new();
    private readonly RemoteReceiver _receiver;

    public PlayerRemoteControlTests() => _receiver = RemoteHarness.StandAlone(_receiverThread, _time);

    private FakeServerPlayback Playback => _playbacks[^1];

    private PlayerViewModel NewPlayer(bool episode = false) =>
        new(_engine, new ImmediateDispatcher(), _navigation, () =>
        {
            var playback = new FakeServerPlayback
            {
                DurationSeconds = Length,
                Episode = episode ? Episode : null,
                MediaInfo = episode
                    ? new PlaybackMediaInfo("Pilot", "Example Series — S1 · E3", Poster) { Kind = PlaybackMediaKind.Episode }
                    : new PlaybackMediaInfo("Example Movie", "2024", Poster) { Kind = PlaybackMediaKind.Movie },
            };
            _playbacks.Add(playback);
            return playback;
        }, new PlaybackStops(), _time, _episodes);

    // A title that plays at 120 s, with its tracks and qualities, and a remote on it as the player page makes one.
    private async Task<(PlayerViewModel Player, PlayerRemoteControl Remote)> PlayingAsync(bool episode = false, bool paused = false,
        string? subtitle = null)
    {
        var player = NewPlayer(episode);
        await player.StartAsync(PlayRequest.Resume(episode ? EpisodeId : MovieId));
        var remote = new PlayerRemoteControl(player, _receiver, new ImmediateDispatcher());
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        Playback.RaiseTracks(Audio, 0);
        Playback.RaiseSubtitles(Subtitles, subtitle);
        Playback.RaisePlan(Qualities, QualityOption.Auto, "1080p");
        At(120, paused);
        // The status as the receiver reads it for the phones, which their numbers refer to.
        _ = remote.State;
        return (player, remote);
    }

    private void At(double seconds, bool paused = false, bool buffering = false) =>
        _engine.RaiseState(new PlaybackSnapshot(true, paused, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(Length), null, HasStarted: true,
            IsBuffering: buffering));

    private static SiloCastMessage.Control Command(string name, double? seconds = null, long? trackId = null, double? speed = null,
        double? volume = null, string? value = null, bool? enabled = null, int? milliseconds = null) =>
        new(name, seconds, trackId, speed, volume, value, enabled, milliseconds);

    [Fact]
    public async Task TheStatus_ShowsTheTitleItsPositionAndLength()
    {
        var (_, remote) = await PlayingAsync(episode: true);

        var state = remote.State;

        Assert.Equal((EpisodeId, "Pilot", "Example Series — S1 · E3"), (state.ContentId, state.Title, state.Subtitle));
        Assert.Equal((true, false, false, 120.0, Length), (state.IsPlaying, state.IsLoading, state.IsBuffering, state.CurrentTime, state.Duration));
        Assert.Equal((1.0, false), (state.Volume, state.IsMuted));
    }

    [Fact]
    public async Task AMovie_HasNoEpisodeLine()
    {
        var (_, remote) = await PlayingAsync();

        Assert.Equal((MovieId, "Example Movie", null), (remote.State.ContentId, remote.State.Title, remote.State.Subtitle));
    }

    [Fact]
    public async Task ATitleThatStillLoads_ShowsAsLoading_WithItsContentId()
    {
        var player = NewPlayer();
        await player.StartAsync(PlayRequest.Resume(MovieId));
        using var remote = new PlayerRemoteControl(player, _receiver, new ImmediateDispatcher());

        var state = remote.State;

        Assert.Equal((MovieId, true, false), (state.ContentId, state.IsLoading, state.IsPlaying));
        Assert.Equal("Loading", state.Title);
    }

    [Fact]
    public async Task APausedTitle_IsNotPlaying()
    {
        var (_, remote) = await PlayingAsync(paused: true);

        Assert.Equal((false, false), (remote.State.IsPlaying, remote.State.IsLoading));
    }

    [Fact]
    public async Task ABufferingTitle_SaysSo()
    {
        var (_, remote) = await PlayingAsync();

        At(121, buffering: true);

        Assert.True(remote.State.IsBuffering);
    }

    [Fact]
    public async Task AFilePlayedDirectly_ShowsAsIdle_AndTakesNoCommand()
    {
        var player = NewPlayer();
        await player.StartAsync(PlayRequest.Direct(@"C:\Videos\Holiday.mkv"));
        using var remote = new PlayerRemoteControl(player, _receiver, new ImmediateDispatcher());
        At(5);

        Assert.Same(SiloCastState.Idle, remote.State);
        Assert.Equal(RemoteCommandResult.NotReady, remote.Apply(Command(SiloCastControls.Pause)));
        Assert.Equal(RemoteCommandResult.NotReady, remote.Apply(Command(SiloCastControls.Seek, seconds: 60)));
        Assert.Equal(RemoteCommandResult.NotReady, remote.Apply(Command(SiloCastControls.Stop)));
        Assert.DoesNotContain(true, _engine.PauseRequests);
        Assert.Empty(_engine.Seeks);
        Assert.False(player.HasLeft);
    }

    [Fact]
    public async Task AudioTracks_CarryTheirOrdinal_AndSubtitlesTheirPositionInTheList()
    {
        var (player, remote) = await PlayingAsync(subtitle: Subtitles[1].TrackId);

        var state = remote.State;

        Assert.Equal([("audio", 0L, player.AudioTracks[0].Title), ("audio", 1L, player.AudioTracks[1].Title)],
            state.AudioTracks.Select(t => (t.Kind, t.TrackId, t.Title)));
        Assert.Equal(0, state.SelectedAudioTrackId);
        // Off is no track: the phone sends none for it.
        Assert.Equal([("subtitle", 0L), ("subtitle", 1L), ("subtitle", 2L)], state.SubtitleTracks.Select(t => (t.Kind, t.TrackId)));
        Assert.Equal(player.SubtitleTracks.Skip(1).Select(row => row.Title), state.SubtitleTracks.Select(t => t.Title));
        Assert.Equal(1, state.SelectedSubtitleTrackId);
    }

    [Fact]
    public async Task NoSubtitleShowing_SelectsNoSubtitleTrack()
    {
        var (_, remote) = await PlayingAsync();

        Assert.Null(remote.State.SelectedSubtitleTrackId);
        Assert.Null(remote.State.SubtitleSyncMs);
        Assert.False(remote.State.SupportsSubtitleDelay);
    }

    [Fact]
    public async Task TheQualityOptions_AreListed_WithTheSelectedOnesId()
    {
        var (_, remote) = await PlayingAsync();
        Playback.RaisePlan(Qualities, "1080p", "1080p");

        var state = remote.State;

        Assert.Equal([("auto", "Auto", null), ("1080p", "1080p High", "8 Mbps"), ("720p", "720p", "4 Mbps")],
            state.QualityOptions.Select(q => (q.Id, q.Label, q.Detail)));
        Assert.Equal("1080p", state.ActiveQualityId);
    }

    [Fact]
    public async Task Volume100_Is1_AndMuteIsItsOwnFlag()
    {
        var (player, remote) = await PlayingAsync();
        Assert.Equal((1.0, false), (remote.State.Volume, remote.State.IsMuted));

        player.ToggleMuteCommand.Execute(null);
        Assert.Equal((1.0, true), (remote.State.Volume, remote.State.IsMuted));

        player.Volume = 40;
        Assert.Equal((0.4, false), (remote.State.Volume, remote.State.IsMuted));
    }

    [Fact]
    public async Task TheStatus_NeverCarriesASessionIdAPosterOrAnOverview()
    {
        var (_, remote) = await PlayingAsync(episode: true);

        string json = Encoding.UTF8.GetString(SiloCastMessages.State(remote.State));

        Assert.DoesNotContain("sessionId", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Playback.SessionId!, json, StringComparison.Ordinal);
        Assert.DoesNotContain("posterUrl", json, StringComparison.Ordinal);
        Assert.DoesNotContain("artwork", json, StringComparison.Ordinal);
        Assert.DoesNotContain("overview", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SiloCastControls.Play, null)]
    [InlineData(SiloCastControls.Pause, null)]
    [InlineData(SiloCastControls.PlayPause, null)]
    [InlineData(SiloCastControls.Seek, null)]
    [InlineData(SiloCastControls.SetVolume, null)]
    [InlineData(SiloCastControls.SetMuted, null)]
    [InlineData(SiloCastControls.SelectAudioTrack, null)]
    [InlineData(SiloCastControls.SetPlaybackSpeed, null)]
    [InlineData(SiloCastControls.SetQuality, null)]
    [InlineData(SiloCastControls.SetVideoGravity, "fill")]
    [InlineData(SiloCastControls.SetVideoGravity, "fit")]
    [InlineData(SiloCastControls.SetSubtitleSyncMs, null)]
    [InlineData(SiloCastControls.PlayNext, null)]
    [InlineData(SiloCastControls.Stop, null)]
    public async Task EachCommand_DoesWhatThePcsOwnButtonDoes(string name, string? value)
    {
        _episodes.Next = new NextEpisode(NextEpisodeId, 1, 4, "The Next One");
        var (player, remote) = await PlayingAsync(episode: true, paused: name == SiloCastControls.Play, subtitle: Subtitles[0].TrackId);
        if (value == "fit")
        {
            player.ToggleFillCommand.Execute(null);
        }
        var playback = Playback;

        var command = name switch
        {
            SiloCastControls.Seek => Command(name, seconds: 600),
            SiloCastControls.SetVolume => Command(name, volume: 0.4),
            SiloCastControls.SetMuted => Command(name, enabled: true),
            SiloCastControls.SelectAudioTrack => Command(name, trackId: 1),
            SiloCastControls.SetPlaybackSpeed => Command(name, speed: 1.25),
            SiloCastControls.SetQuality => Command(name, value: "720p"),
            SiloCastControls.SetVideoGravity => Command(name, value: value),
            SiloCastControls.SetSubtitleSyncMs => Command(name, milliseconds: -250),
            _ => Command(name),
        };

        Assert.Equal(RemoteCommandResult.Done, remote.Apply(command));

        switch (name)
        {
            case SiloCastControls.Play:
                Assert.False(_engine.Current.IsPaused);
                Assert.False(_engine.PauseRequests[^1]);
                break;
            case SiloCastControls.Pause or SiloCastControls.PlayPause:
                Assert.True(_engine.Current.IsPaused);
                break;
            case SiloCastControls.Seek:
                Assert.Equal([600.0], playback.Seeks);
                break;
            case SiloCastControls.SetVolume:
                Assert.Equal((40.0, 40.0), (player.Volume, _engine.VolumeRequests[^1]));
                break;
            case SiloCastControls.SetMuted:
                Assert.True(player.IsMuted);
                Assert.True(_engine.MuteRequests[^1]);
                break;
            case SiloCastControls.SelectAudioTrack:
                Assert.Equal([1], playback.AudioPicks);
                break;
            case SiloCastControls.SetPlaybackSpeed:
                Assert.Equal(1.25, playback.Speed);
                break;
            case SiloCastControls.SetQuality:
                Assert.Equal(["720p"], playback.QualityPicks);
                break;
            case SiloCastControls.SetVideoGravity:
                Assert.Equal(value == "fill", player.IsFillingScreen);
                Assert.Equal(value == "fill", _engine.Fill);
                break;
            case SiloCastControls.SetSubtitleSyncMs:
                Assert.Equal(-250, playback.SubtitleDelayMs);
                break;
            case SiloCastControls.PlayNext:
                Assert.Equal(PlayRequest.ResumeWithoutRewind(NextEpisodeId), Playback.Started);
                break;
            case SiloCastControls.Stop:
                Assert.True(player.HasLeft);
                Assert.Equal(1, _navigation.PlayerLeaves);
                break;
        }
    }

    [Fact]
    public async Task Stop_ClosesThePlayer_AlsoWhileATitleStillLoads()
    {
        var player = NewPlayer();
        await player.StartAsync(PlayRequest.Resume(MovieId));
        using var remote = new PlayerRemoteControl(player, _receiver, new ImmediateDispatcher());

        Assert.Equal(RemoteCommandResult.NotReady, remote.Apply(Command(SiloCastControls.Pause)));
        Assert.Equal(RemoteCommandResult.Done, remote.Apply(Command(SiloCastControls.Stop)));

        Assert.True(player.HasLeft);
        Assert.Equal(1, _navigation.PlayerLeaves);
    }

    [Fact]
    public async Task ASubtitleNumber_GoesBackToTheTrackItWasSentFor()
    {
        var (_, remote) = await PlayingAsync();
        _ = remote.State;

        Assert.Equal(RemoteCommandResult.Done, remote.Apply(Command(SiloCastControls.SelectSubtitleTrack, trackId: 2)));

        Assert.Equal([Subtitles[2].TrackId], Playback.SubtitlePicks);
    }

    [Fact]
    public async Task ASubtitleNumber_FromAnOlderList_IsIgnored_WhenThatTrackIsGone()
    {
        var (_, remote) = await PlayingAsync();
        _ = remote.State;
        // A new plan lists one track fewer before the phone was shown it.
        Playback.RaiseSubtitles([Subtitles[0], Subtitles[2]], null);

        Assert.Equal(RemoteCommandResult.Ignored, remote.Apply(Command(SiloCastControls.SelectSubtitleTrack, trackId: 1)));
        Assert.Empty(Playback.SubtitlePicks);

        // The number the phone was shown for a track that is still there still names that track.
        Assert.Equal(RemoteCommandResult.Done, remote.Apply(Command(SiloCastControls.SelectSubtitleTrack, trackId: 2)));
        Assert.Equal([Subtitles[2].TrackId], Playback.SubtitlePicks);
    }

    [Fact]
    public async Task NoTrackId_TurnsSubtitlesOff()
    {
        var (_, remote) = await PlayingAsync(subtitle: Subtitles[1].TrackId);

        Assert.Equal(RemoteCommandResult.Done, remote.Apply(Command(SiloCastControls.SelectSubtitleTrack)));

        Assert.Equal([null], Playback.SubtitlePicks);
    }

    [Fact]
    public async Task PlayWhilePlaying_AndPauseWhilePaused_ChangeNothing()
    {
        var (_, remote) = await PlayingAsync();

        Assert.Equal(RemoteCommandResult.Ignored, remote.Apply(Command(SiloCastControls.Play)));
        Assert.Empty(_engine.PauseRequests);

        At(120, paused: true);
        Assert.Equal(RemoteCommandResult.Ignored, remote.Apply(Command(SiloCastControls.Pause)));
        Assert.Empty(_engine.PauseRequests);
    }

    [Fact]
    public async Task AVolumeOutsideZeroToOne_IsBroughtIntoIt()
    {
        var (player, remote) = await PlayingAsync();

        Assert.Equal(RemoteCommandResult.Done, remote.Apply(Command(SiloCastControls.SetVolume, volume: -3)));
        Assert.Equal(0, player.Volume);

        Assert.Equal(RemoteCommandResult.Done, remote.Apply(Command(SiloCastControls.SetVolume, volume: 2.5)));
        Assert.Equal(100, player.Volume);
        Assert.All(_engine.VolumeRequests, percent => Assert.InRange(percent, 0, 100));
    }

    [Fact]
    public async Task ASeekPastTheEnd_IsBroughtIntoTheTitle()
    {
        var (_, remote) = await PlayingAsync();

        Assert.Equal(RemoteCommandResult.Done, remote.Apply(Command(SiloCastControls.Seek, seconds: Length + 500)));

        Assert.Equal([Length], Playback.Seeks);
    }

    [Fact]
    public async Task ASubtitleDelay_IsClampedToThePlayersLimits()
    {
        var (_, remote) = await PlayingAsync(subtitle: Subtitles[0].TrackId);

        Assert.Equal(RemoteCommandResult.Done, remote.Apply(Command(SiloCastControls.SetSubtitleSyncMs, milliseconds: 50_000)));

        Assert.Equal([PlaybackPreferencesReader.MaxSubtitleDelayMs], Playback.SubtitleDelayRequests);
        Assert.Equal(PlaybackPreferencesReader.MaxSubtitleDelayMs, remote.State.SubtitleSyncMs);
    }

    [Fact]
    public async Task PlayNext_WithoutANextEpisode_Fails()
    {
        var (_, remote) = await PlayingAsync();

        Assert.Equal(RemoteCommandResult.Failed, remote.Apply(Command(SiloCastControls.PlayNext)));

        Assert.False(remote.State.HasNextEpisode);
        Assert.Single(_playbacks);
    }

    [Fact]
    public async Task TheNextEpisode_IsNamed()
    {
        _episodes.Next = new NextEpisode(NextEpisodeId, 1, 4, "The Next One");
        var (_, remote) = await PlayingAsync(episode: true);

        Assert.Equal((true, "The Next One"), (remote.State.HasNextEpisode, remote.State.NextEpisodeTitle));
    }

    [Fact]
    public async Task ACommandWithoutItsArgument_IsIgnored()
    {
        var (player, remote) = await PlayingAsync(subtitle: Subtitles[0].TrackId);
        _engine.VolumeRequests.Clear();
        _engine.MuteRequests.Clear();

        string[] names =
        [
            SiloCastControls.Seek, SiloCastControls.SetVolume, SiloCastControls.SetMuted, SiloCastControls.SelectAudioTrack,
            SiloCastControls.SetPlaybackSpeed, SiloCastControls.SetQuality, SiloCastControls.SetVideoGravity, SiloCastControls.SetSubtitleSyncMs,
        ];

        Assert.All(names, name => Assert.Equal(RemoteCommandResult.Ignored, remote.Apply(Command(name))));
        Assert.Equal(RemoteCommandResult.Ignored, remote.Apply(Command(SiloCastControls.SetQuality, value: "")));
        Assert.Equal(RemoteCommandResult.Ignored, remote.Apply(Command(SiloCastControls.SetVideoGravity, value: "stretch")));
        Assert.Equal(RemoteCommandResult.Ignored, remote.Apply(Command(SiloCastControls.SetQuality, value: "4320p")));
        Assert.Empty(Playback.Seeks);
        Assert.Empty(_engine.VolumeRequests);
        Assert.Empty(_engine.MuteRequests);
        Assert.Empty(Playback.AudioPicks);
        Assert.Empty(Playback.SpeedRequests);
        Assert.Empty(Playback.QualityPicks);
        Assert.Empty(Playback.SubtitleDelayRequests);
        Assert.False(player.IsFillingScreen);
    }

    [Fact]
    public async Task AnUnknownCommand_IsIgnored()
    {
        var (_, remote) = await PlayingAsync();

        Assert.Equal(RemoteCommandResult.Ignored, remote.Apply(Command("set_hdr", enabled: true)));
    }

    [Fact]
    public async Task SubtitlePosition_IsNotSupported()
    {
        var (_, remote) = await PlayingAsync(subtitle: Subtitles[0].TrackId);

        Assert.False(remote.State.SupportsSubtitlePosition);
        Assert.Equal(RemoteCommandResult.Ignored, remote.Apply(Command(SiloCastControls.SetSubtitlePosition, value: "top")));
    }

    [Fact]
    public async Task AChangeInThePlayer_RaisesStateChanged_ButThePositionAloneDoesNot()
    {
        var (player, remote) = await PlayingAsync();
        int changes = 0;
        remote.StateChanged += (_, _) => changes++;

        At(121);
        At(122);
        Assert.Equal(0, changes);

        At(122, paused: true);
        Assert.Equal(1, changes);

        player.Volume = 30;
        Assert.True(changes > 1);
    }

    [Fact]
    public async Task AfterDispose_TheReceiverHasNoPlayer()
    {
        var (_, remote) = await PlayingAsync();
        Assert.Same(remote, _receiver.AttachedPlayer);
        int changes = 0;
        remote.StateChanged += (_, _) => changes++;

        remote.Dispose();

        Assert.Null(_receiver.AttachedPlayer);
        At(120, paused: true);
        Assert.Equal(0, changes);
    }

    [Theory]
    [InlineData(SiloCastControls.Seek, -5d, 0d)]
    [InlineData(SiloCastControls.Seek, 1e308, Length)]
    [InlineData(SiloCastControls.Seek, double.NaN, null)]
    [InlineData(SiloCastControls.Seek, double.NegativeInfinity, null)]
    [InlineData(SiloCastControls.SetVolume, -1d, 0d)]
    [InlineData(SiloCastControls.SetVolume, 7d, 1d)]
    [InlineData(SiloCastControls.SetVolume, double.PositiveInfinity, null)]
    [InlineData(SiloCastControls.SetPlaybackSpeed, 0d, null)]
    [InlineData(SiloCastControls.SetPlaybackSpeed, -2d, null)]
    [InlineData(SiloCastControls.SetPlaybackSpeed, double.NaN, null)]
    [InlineData(SiloCastControls.SetPlaybackSpeed, 1e9, 3d)]
    [InlineData(SiloCastControls.SetSubtitleSyncMs, -2147483648d, -10_000d)]
    [InlineData(SiloCastControls.SetSubtitleSyncMs, 2147483647d, 10_000d)]
    [InlineData(SiloCastControls.SelectAudioTrack, -1d, null)]
    [InlineData(SiloCastControls.SelectAudioTrack, 1099511627776d, null)]
    [InlineData(SiloCastControls.SelectAudioTrack, 2d, null)]
    [InlineData(SiloCastControls.SelectSubtitleTrack, -1d, null)]
    [InlineData(SiloCastControls.SelectSubtitleTrack, 1099511627776d, null)]
    [InlineData(SiloCastControls.SelectSubtitleTrack, 3d, null)]
    public async Task EveryNumberFromAPhone_IsRangeCheckedOrClamped(string name, double number, double? expected)
    {
        var (player, remote) = await PlayingAsync(subtitle: Subtitles[0].TrackId);
        player.Volume = 50;
        _ = remote.State;
        _engine.VolumeRequests.Clear();
        _engine.MuteRequests.Clear();
        var command = name switch
        {
            SiloCastControls.Seek => Command(name, seconds: number),
            SiloCastControls.SetVolume => Command(name, volume: number),
            SiloCastControls.SetPlaybackSpeed => Command(name, speed: number),
            SiloCastControls.SetSubtitleSyncMs => Command(name, milliseconds: (int)number),
            _ => Command(name, trackId: (long)number),
        };

        var result = remote.Apply(command);

        if (expected is not { } value)
        {
            Assert.Equal(RemoteCommandResult.Ignored, result);
            Assert.Empty(Playback.Seeks);
            Assert.Empty(_engine.Seeks);
            Assert.Empty(_engine.VolumeRequests);
            Assert.Empty(Playback.SpeedRequests);
            Assert.Empty(Playback.SubtitleDelayRequests);
            Assert.Empty(Playback.AudioPicks);
            Assert.Empty(Playback.SubtitlePicks);
            return;
        }
        Assert.Equal(RemoteCommandResult.Done, result);
        switch (name)
        {
            case SiloCastControls.Seek:
                Assert.Equal([value], Playback.Seeks);
                break;
            case SiloCastControls.SetVolume:
                Assert.Equal([value * 100], _engine.VolumeRequests);
                Assert.Equal(value, remote.State.Volume);
                break;
            case SiloCastControls.SetPlaybackSpeed:
                Assert.Equal([value], Playback.SpeedRequests);
                break;
            case SiloCastControls.SetSubtitleSyncMs:
                Assert.Equal([(int)value], Playback.SubtitleDelayRequests);
                break;
        }
        Assert.Empty(_engine.Seeks);
    }

    /// <summary>The episodes around the one that plays, as the test sets them.</summary>
    private sealed class EpisodesAround : IUpNextSource
    {
        public NextEpisode? Next { get; set; }

        public Task<NextEpisode?> NextEpisodeAsync(PlayingEpisode current, CancellationToken cancellationToken) => Task.FromResult(Next);

        public Task<NextEpisode?> PreviousEpisodeAsync(PlayingEpisode current, CancellationToken cancellationToken) =>
            Task.FromResult<NextEpisode?>(null);

        public Task<IReadOnlyList<MediaCard>> OnDeckAsync(string? exceptSeriesId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaCard>>([]);
    }

    /// <summary>A UI thread the test is not on: what is posted to it waits, and runs only if the test says so.</summary>
    private sealed class HeldDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _work = new();

        public bool HasThreadAccess => false;

        public void Post(Action action) => _work.Enqueue(action);
    }
}
