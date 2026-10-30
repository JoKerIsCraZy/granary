using System.Net;
using System.Text.Json;
using Granary.Core.Abstractions;
using Granary.Core.Api.Playback;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Player;
using Granary.Core.WatchParty;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.WatchParty;

/// <summary>The player while a Watch Party plays its title: the room's file from the start, and the viewer's controls as requests.</summary>
public sealed class PlayerViewModelWatchPartyTests : IAsyncDisposable
{
    private const string Room = "api/v2/watch-together/rooms/" + WatchPartyPayloads.RoomId;

    private readonly ScriptedSilo _silo = new();
    private readonly FakeSessionManager _session = new() { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" };
    private readonly LogSink _logs = new();
    private readonly List<FakeRealtimeSocket> _sockets = [];
    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly RecordingNavigation _navigation = new();
    private readonly List<FakeServerPlayback> _playbacks = [];
    private readonly FakeConfirmation _confirmation = new();
    private readonly WatchPartySession _party;

    public PlayerViewModelWatchPartyTests()
    {
        _silo.On(HttpMethod.Post, Room + "/ws-ticket", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.TicketAnswer));
        var api = new WatchPartyApi(_silo.Http);
        _party = new WatchPartySession(_session, api, () => new WatchPartyConnection(api, () =>
        {
            var socket = new FakeRealtimeSocket { Picks = WatchPartyApi.Protocol };
            lock (_sockets)
            {
                _sockets.Add(socket);
            }
            return socket;
        }, TimeProvider.System, _logs.For<WatchPartyConnection>()), new ImmediateDispatcher(), _logs.For<WatchPartySession>());
    }

    private FakeServerPlayback Playback => _playbacks[^1];

    private FakeRealtimeSocket Socket => _sockets[^1];

    private void At(double seconds)
    {
        _time.Advance(TimeSpan.FromSeconds(0.25));
        _engine.RaiseState(new PlaybackSnapshot(true, false, TimeSpan.FromSeconds(seconds), TimeSpan.FromHours(2), null, HasStarted: true));
    }

    private async Task<PlayerViewModel> PlayingAsync(string role = "host", MarkerSettings? markers = null, bool transport = true)
    {
        string playing = WatchPartyPayloads.Room("playing", "playing", generation: 3, revision: 1, role: role, selected: "movie:heat", transport: transport);
        _silo.On(HttpMethod.Post, "api/v2/watch-together/join", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(playing)));
        await _party.JoinAsync("ABCD1234", TestContext.Current.CancellationToken);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && _party.IsConnected, "the room connection");
        var viewModel = new PlayerViewModel(_engine, new ImmediateDispatcher(), _navigation, () =>
        {
            var playback = new FakeServerPlayback
            {
                DurationSeconds = 7200,
                Preferences = PlaybackPreferences.Default with { Markers = markers ?? MarkerSettings.Default },
            };
            _playbacks.Add(playback);
            return playback;
        }, new PlaybackStops(), _time, watchParty: _party, confirmation: _confirmation);
        await viewModel.StartAsync(PlayRequest.WatchParty("movie:heat", "77", 1));
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        _engine.RaiseState(new PlaybackSnapshot(true, false, TimeSpan.FromSeconds(30), TimeSpan.FromHours(2), null, HasStarted: true));
        return viewModel;
    }

    [Fact]
    public async Task TheRoomsTitle_PlaysItsFile_FromTheStart()
    {
        var viewModel = await PlayingAsync();

        Assert.Equal(("movie:heat", "77", true, true), (Playback.Started!.ContentId, Playback.Started.PinnedFileId, Playback.Started.FromBeginning,
            Playback.Started.IsWatchParty));
        Assert.True(viewModel.IsWatchParty);
        Assert.Empty(viewModel.VersionOptions);
    }

    [Fact]
    public async Task Pause_IsAskedOfTheRoom_NotDoneHere()
    {
        var viewModel = await PlayingAsync();

        viewModel.TogglePauseCommand.Execute(null);

        Assert.False(_engine.Current.IsPaused);
        await ServerPlaybackFixture.Until(() => Socket.Sent.Any(f => f.Contains("\"transport_request\"", StringComparison.Ordinal)), "the request");
        Assert.Contains(Socket.Sent, f => f.Contains("\"action\":\"pause\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATrailer_LeavesTheRoomsPlaybackAlone()
    {
        var viewModel = await PlayingAsync();

        Assert.False(viewModel.PauseForTrailer());

        Assert.False(_engine.Current.IsPaused);
        Assert.DoesNotContain(Socket.Sent, f => f.Contains("\"transport_request\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGuestsSeek_IsRefused_WithTheWebsWords()
    {
        var viewModel = await PlayingAsync(role: "guest");

        viewModel.SeekTo(600);

        Assert.Empty(Playback.Seeks);
        Assert.Equal(WatchPartyPlaybackBinding.HostSeeksText, viewModel.NoticeMessage);
        Assert.False(viewModel.CanSeekInRoom);
    }

    [Fact]
    public async Task InARoom_NoPartSkipsItself_AndTheHostsSkipIsAskedOfTheRoom()
    {
        var viewModel = await PlayingAsync(markers: MarkerSettings.Default with
        {
            IntroSkipMode = MarkerSettings.Always,
            AutoSkipRecap = true,
            AutoSkipCredits = true,
        });
        Playback.RaiseMarkers([new PlaybackMarker(WatchMarkerKinds.Recap, 10, 50), new PlaybackMarker(WatchMarkerKinds.Intro, 60, 120)]);

        At(11);
        At(61);

        Assert.Empty(Playback.Seeks);
        Assert.Null(viewModel.Notice);
        Assert.DoesNotContain(Socket.Sent, f => f.Contains("\"transport_request\"", StringComparison.Ordinal));
        Assert.True(viewModel.HasSkipPrompt);
        Assert.Equal("Skip Intro", viewModel.SkipLabel);

        viewModel.SkipCommand.Execute(null);
        await ServerPlaybackFixture.Until(() => Socket.Sent.Any(f => f.Contains("\"transport_request\"", StringComparison.Ordinal)), "the request");
        var request = JsonDocument.Parse(Socket.Sent.First(f => f.Contains("\"transport_request\"", StringComparison.Ordinal))).RootElement;
        Assert.Equal(("seek", 120.0), (request.GetProperty("action").GetString(), request.GetProperty("position_seconds").GetDouble()));
        Assert.Empty(Playback.Seeks);
    }

    [Fact]
    public async Task AnIntroTheProfileNeverSkips_IsNotOfferedInARoomEither()
    {
        var viewModel = await PlayingAsync(markers: MarkerSettings.Default with { IntroSkipMode = MarkerSettings.Never });
        Playback.RaiseMarkers([new PlaybackMarker(WatchMarkerKinds.Intro, 60, 120)]);

        At(61);

        Assert.False(viewModel.HasSkipPrompt);
        Assert.Empty(Playback.Seeks);
    }

    [Fact]
    public async Task AGuest_IsNotOfferedASkip_TheyCouldNotMake()
    {
        var viewModel = await PlayingAsync(role: "guest");
        Playback.RaiseMarkers([new PlaybackMarker(WatchMarkerKinds.Intro, 60, 120)]);

        At(61);

        Assert.False(viewModel.HasSkipPrompt);
        Assert.Null(viewModel.Notice);
    }

    [Fact]
    public async Task TheRoomBackInItsLobby_ClosesThePlayer()
    {
        await PlayingAsync();

        Socket.Push(WatchPartyPayloads.Snapshot(WatchPartyPayloads.Room("lobby", "idle", generation: 4, revision: 2, selected: "movie:heat")));
        await ServerPlaybackFixture.Until(() => _navigation.PlayerLeaves == 1, "the player leaving");

        Assert.Equal(1, Playback.Stops);
    }

    [Fact]
    public async Task ThePanel_FollowsTheRoom_AndSaysWhatItDid_AsTheNotice()
    {
        var viewModel = await PlayingAsync();
        var panel = viewModel.Party!;

        Assert.Equal(("ABCD1234", "2 viewers"), (panel.Code, panel.ViewersText));

        panel.CopyInvite(_ => true);

        Assert.Equal(("Watch Party", "Invite copied. Room code ABCD1234", false), (viewModel.NoticeTitle, viewModel.NoticeMessage, viewModel.IsNoticeWarning));
    }

    [Fact]
    public async Task ThePanel_LetsGoOfTheRoom_WhenThePlayerCloses()
    {
        var viewModel = await PlayingAsync();
        viewModel.Leave();

        Socket.Push(WatchPartyPayloads.Snapshot(WatchPartyPayloads.Room("playing", "playing", generation: 4, revision: 1, selected: "movie:heat", count: 3)));
        await ServerPlaybackFixture.Until(() => _party.Room?.MemberCount == 3, "the new snapshot");

        Assert.Equal("2 viewers", viewModel.Party!.ViewersText);
        Assert.False(viewModel.Party.IsShown);
    }

    [Fact]
    public async Task StopFromThePanel_ClosesThePlayer_BackToTheRoom()
    {
        var viewModel = await PlayingAsync();
        _silo.On(HttpMethod.Post, Room + "/playback/stop", ScriptedSilo.Json(HttpStatusCode.OK,
            WatchPartyPayloads.Answer(WatchPartyPayloads.Room(generation: 4, revision: 1, selected: "movie:heat"))));

        await viewModel.Party!.StopCommand.ExecuteAsync(null);

        Assert.Equal(1, _navigation.PlayerLeaves);
        Assert.Empty(_navigation.Navigations);
        Assert.Equal(1, Playback.Stops);
        Assert.True(_party.IsInRoom);
    }

    [Fact]
    public async Task EndFromThePanel_AsksFirst_ThenClosesThePlayer_AndGoesToWatchPartyOnce()
    {
        var viewModel = await PlayingAsync();
        _silo.On(HttpMethod.Delete, Room, ScriptedSilo.NoContent());

        await viewModel.Party!.EndCommand.ExecuteAsync(null);

        Assert.Single(_confirmation.Asked);
        Assert.False(_party.IsInRoom);
        Assert.Equal(1, _navigation.PlayerLeaves);
        Assert.Equal([AppRoute.WatchParty], _navigation.Navigations.Select(n => n.Route));
        Assert.Equal(1, Playback.Stops);
    }

    [Fact]
    public async Task AFailedEnd_KeepsThePlayer_AndSaysSo()
    {
        var viewModel = await PlayingAsync();
        _silo.On(HttpMethod.Delete, Room, ScriptedSilo.Problem(500, "internal"));

        await viewModel.Party!.EndCommand.ExecuteAsync(null);

        Assert.Equal(0, _navigation.PlayerLeaves);
        Assert.Equal(("Watch Party", "Failed to end room", true), (viewModel.NoticeTitle, viewModel.NoticeMessage, viewModel.IsNoticeWarning));
    }

    [Fact]
    public async Task InARoom_WindowsMediaControlsOfferNoEpisodes_AndTheHostsPauseIsAskedOfTheRoom()
    {
        var viewModel = await PlayingAsync();
        var controls = new FakeSystemMediaControls();
        using var media = new PlayerSystemMedia(viewModel, controls, new ImmediateDispatcher(), _time);

        Assert.Equal((true, true, false, false),
            (controls.Current!.CanPlayPause, controls.Current.CanSeek, controls.Current.CanGoPrevious, controls.Current.CanGoNext));

        controls.Press(SystemMediaButton.Pause);

        Assert.False(_engine.Current.IsPaused);
        await ServerPlaybackFixture.Until(() => Socket.Sent.Any(f => f.Contains("\"action\":\"pause\"", StringComparison.Ordinal)), "the request");
    }

    [Fact]
    public async Task AGuestTheRoomDoesNotLetPause_HasNeitherPlayPauseNorSeekingOnWindowsMediaControls_AndTheirKeysDoNothing()
    {
        var viewModel = await PlayingAsync(role: "guest", transport: false);
        var controls = new FakeSystemMediaControls();
        using var media = new PlayerSystemMedia(viewModel, controls, new ImmediateDispatcher(), _time);

        Assert.Equal((false, false), (controls.Current!.CanPlayPause, controls.Current.CanSeek));

        controls.Press(SystemMediaButton.Pause);
        controls.Press(SystemMediaButton.Seek, 600);
        controls.Press(SystemMediaButton.FastForward);

        Assert.False(_engine.Current.IsPaused);
        Assert.Empty(Playback.Seeks);
        Assert.Null(viewModel.Notice);
        Assert.DoesNotContain(Socket.Sent, f => f.Contains("\"transport_request\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AKeyPressedBeforeTheRoomTookAGuestsPauseAway_AsksNothingWhenItIsHandled()
    {
        var viewModel = await PlayingAsync(role: "guest");
        var controls = new FakeSystemMediaControls();
        var dispatcher = new QueuedDispatcher();
        using var media = new PlayerSystemMedia(viewModel, controls, dispatcher, _time);
        controls.Press(SystemMediaButton.Pause);

        Socket.Push(WatchPartyPayloads.Snapshot(WatchPartyPayloads.Room("playing", "playing", generation: 4, revision: 1, role: "guest",
            selected: "movie:heat", transport: false)));
        await ServerPlaybackFixture.Until(() => !viewModel.CanPauseInRoom, "the room's new policy");
        dispatcher.RunAll();

        Assert.False(_engine.Current.IsPaused);
        Assert.DoesNotContain(Socket.Sent, f => f.Contains("\"transport_request\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APauseInTheMomentTheRoomStopsPlaying_IsNotDoneHereAlone()
    {
        bool armed = false;
        bool? changedHere = null;
        PlayerViewModel? player = null;
        // Runs before the player hears of the room: the room plays nothing, and the player has not left yet.
        _party.PropertyChanged += (_, e) =>
        {
            if (armed && changedHere is null && e.PropertyName == nameof(WatchPartySession.Room) && _party.Room is not { IsPlaying: true })
            {
                bool paused = _engine.Current.IsPaused;
                player!.TogglePauseCommand.Execute(null);
                changedHere = _engine.Current.IsPaused != paused;
            }
        };
        player = await PlayingAsync(role: "guest");
        armed = true;

        Socket.Push(WatchPartyPayloads.Snapshot(WatchPartyPayloads.Room("lobby", "idle", generation: 4, revision: 1, role: "guest",
            selected: "movie:heat")));
        await ServerPlaybackFixture.Until(() => changedHere is not null, "the room's stop");

        Assert.False(changedHere);
    }

    [Fact]
    public async Task AGuestTheRoomLetsPause_HasPlayPauseButNoSeekingOnWindowsMediaControls()
    {
        var viewModel = await PlayingAsync(role: "guest");
        var controls = new FakeSystemMediaControls();
        using var media = new PlayerSystemMedia(viewModel, controls, new ImmediateDispatcher(), _time);

        Assert.Equal((true, false), (controls.Current!.CanPlayPause, controls.Current.CanSeek));
    }

    public async ValueTask DisposeAsync() => await _party.LeaveAsync();
}
