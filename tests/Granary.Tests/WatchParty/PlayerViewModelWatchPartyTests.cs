using System.Net;
using System.Text.Json;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Playback;
using Granary.Core.Api.Requests;
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
    private readonly InMemorySecretStore _secrets = new();
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
        }, TimeProvider.System, _logs.For<WatchPartyConnection>()), new ImmediateDispatcher(), _logs.For<WatchPartySession>(),
            new WatchPartyRecentRooms(_secrets, _time, _logs.For<WatchPartyRecentRooms>()), _time);
    }

    // In the room, with a player that has not started yet.
    private async Task<PlayerViewModel> JoinedAsync(string room, MarkerSettings? markers = null, IPlaybackDevicePreferences? devicePreferences = null)
    {
        _silo.On(HttpMethod.Post, "api/v2/watch-together/join", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(room)));
        await _party.JoinAsync("ABCD1234", TestContext.Current.CancellationToken);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && _party.IsConnected, "the room connection");
        return new PlayerViewModel(_engine, new ImmediateDispatcher(), _navigation, () =>
        {
            var playback = new FakeServerPlayback
            {
                DurationSeconds = 7200,
                Preferences = PlaybackPreferences.Default with { Markers = markers ?? MarkerSettings.Default },
            };
            _playbacks.Add(playback);
            return playback;
        }, new PlaybackStops(), _time, watchParty: _party, confirmation: _confirmation, devicePreferences: devicePreferences);
    }

    private Task Marked() => ServerPlaybackFixture.Until(
        () => _secrets.Values.TryGetValue(WatchPartyRecentRooms.SecretKey, out string? rooms) && rooms.Contains("\"ended\":true", StringComparison.Ordinal),
        "the recent room marked as ended");

    private FakeServerPlayback Playback => _playbacks[^1];

    private FakeRealtimeSocket Socket => _sockets[^1];

    private void At(double seconds)
    {
        _time.Advance(TimeSpan.FromSeconds(0.25));
        _engine.RaiseState(new PlaybackSnapshot(true, false, TimeSpan.FromSeconds(seconds), TimeSpan.FromHours(2), null, HasStarted: true));
    }

    private async Task<PlayerViewModel> PlayingAsync(string role = "host", MarkerSettings? markers = null, bool transport = true)
    {
        var viewModel = await JoinedAsync(WatchPartyPayloads.Room("playing", "playing", generation: 3, revision: 1, role: role, selected: "movie:heat",
            transport: transport), markers);
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
    public async Task ASleepTimerThatRunsOut_LeavesTheRoom_WhichPlaysOnForTheOthers()
    {
        var viewModel = await PlayingAsync(role: "guest");
        // The room decides what plays next.
        Assert.DoesNotContain(viewModel.SleepTimerOptions, o => o.Action == SleepTimerAction.EndOfTitle);

        viewModel.ChooseSleepTimer(viewModel.SleepTimerOptions.Single(o => o.Label == "5 min"));
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.True(viewModel.HasLeft);
        Assert.Equal(1, _navigation.PlayerLeaves);
        Assert.DoesNotContain(Socket.Sent, f => f.Contains("\"transport_request\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARoomsTitle_StartsWithThisPcsVolume_AndOnlyTheViewersOwnChangeIsKept()
    {
        var preferences = new InMemoryPlaybackDevicePreferences { Volume = new PlayerVolume(40, IsMuted: true) };
        var viewModel = await JoinedAsync(WatchPartyPayloads.Room("playing", "playing", generation: 3, revision: 1, role: "guest", selected: "movie:heat"),
            devicePreferences: preferences);

        await viewModel.StartAsync(PlayRequest.WatchParty("movie:heat", "77", 1));
        Assert.Equal((40.0, true), (_engine.Current.Volume, _engine.Current.IsMuted));
        // The room plays the title: what the engine reports meanwhile is not the viewer's.
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        At(30);
        At(31);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(preferences.SavedVolume);

        viewModel.Volume = 55;
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal([new PlayerVolume(55, IsMuted: false)], preferences.SavedVolume);
    }

    [Fact]
    public async Task AGuestsSeek_IsRefused_WithTheWebsWords()
    {
        var viewModel = await PlayingAsync(role: "guest");

        viewModel.SeekTo(600);

        Assert.Empty(Playback.Seeks);
        Assert.Equal("Only the host can seek the room.", viewModel.NoticeMessage);
        Assert.False(viewModel.CanSeekInRoom);
    }

    [Fact]
    public async Task TheHostsChapterJump_IsAskedOfTheRoom_NotDoneHere()
    {
        var viewModel = await PlayingAsync();
        Playback.RaiseChapters([new PlaybackChapter("Opening", 0, 600, null), new PlaybackChapter("Heist", 600, 7200, null)]);

        viewModel.SeekToAdjacentChapter(forward: true);

        await ServerPlaybackFixture.Until(() => Socket.Sent.Any(f => f.Contains("\"transport_request\"", StringComparison.Ordinal)), "the request");
        var request = JsonDocument.Parse(Socket.Sent.First(f => f.Contains("\"transport_request\"", StringComparison.Ordinal))).RootElement;
        Assert.Equal(("seek", 600.0), (request.GetProperty("action").GetString(), request.GetProperty("position_seconds").GetDouble()));
        Assert.Empty(Playback.Seeks);
    }

    [Fact]
    public async Task AGuestsChapterJump_IsRefused_WithTheWebsWords()
    {
        var viewModel = await PlayingAsync(role: "guest");
        Playback.RaiseChapters([new PlaybackChapter("Opening", 0, 600, null), new PlaybackChapter("Heist", 600, 7200, null)]);

        viewModel.SeekToAdjacentChapter(forward: true);

        Assert.Empty(Playback.Seeks);
        Assert.Equal("Only the host can seek the room.", viewModel.NoticeMessage);
        Assert.DoesNotContain(Socket.Sent, f => f.Contains("\"transport_request\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGuestsDiscordStatus_ShowsTheRoomsTitle_ThoughSeekingIsTheHosts()
    {
        var viewModel = await JoinedAsync(WatchPartyPayloads.Room("playing", "playing", generation: 3, revision: 1, role: "guest", selected: "movie:heat"));
        var presence = new FakeRichPresence();
        var titles = new TmdbTitleLookup(new CatalogApi(_silo.Http), new RequestsApi(_silo.Http), _session, _logs.For<TmdbTitleLookup>());
        using var status = new PlayerRichPresence(viewModel, presence, new InMemoryPlaybackDevicePreferences { DiscordStatus = true }, titles,
            new ImmediateDispatcher(), _time);

        await viewModel.StartAsync(PlayRequest.WatchParty("movie:heat", "77", 1));
        // A title without its catalog id: nothing to look up on TMDB.
        Playback.MediaInfo = new PlaybackMediaInfo("Heat", "1995", null) { Kind = PlaybackMediaKind.Movie, Year = 1995 };
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        _engine.RaiseState(new PlaybackSnapshot(true, false, TimeSpan.FromSeconds(30), TimeSpan.FromHours(2), null, HasStarted: true));

        Assert.False(viewModel.CanSeekInRoom);
        var start = _time.GetUtcNow() - TimeSpan.FromSeconds(30);
        Assert.Equal(new RichPresenceState("Heat", "1995", start, start + TimeSpan.FromHours(2), null, null), presence.Current);
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
    public async Task TheRoomClosingDuringPlayback_LeavesThePlayerAsAnyExitDoes_ForTheHub()
    {
        var viewModel = await PlayingAsync(role: "guest");
        (int EngineStops, double Position)? atStop = null;
        Playback.Stopping = () => atStop = (_engine.Stops, viewModel.PositionSeconds);

        Socket.Push("""{ "type": "room_closed", "reason": "host_left" }""");
        await ServerPlaybackFixture.Until(() => _navigation.PlayerLeaves == 1, "the player leaving");

        // The server playback stops once, as on any exit: asked while the engine still holds the final position.
        Assert.True(viewModel.HasLeft);
        Assert.Equal(1, Playback.Stops);
        Assert.Equal((0, 30d), atStop);
        Assert.Equal(1, _engine.Stops);
        Assert.Equal(AppRoute.WatchParty, Assert.Single(_navigation.Sections));
        Assert.Equal([AppRoute.WatchParty], _navigation.Navigations.Select(n => n.Route));
        await Marked();
    }

    [Fact]
    public async Task ARoomThatEndedBeforeThePlayerBound_LeavesAtOnce_ForTheHub()
    {
        var viewModel = await JoinedAsync(WatchPartyPayloads.Room("playing", "playing", generation: 3, revision: 1, role: "guest", selected: "movie:heat"));
        Socket.Push("""{ "type": "room_closed", "reason": "host_left" }""");
        await ServerPlaybackFixture.Until(() => _party.End is not null, "the end");

        await viewModel.StartAsync(PlayRequest.WatchParty("movie:heat", "77", 1));

        Assert.True(viewModel.HasLeft);
        Assert.Null(Playback.Started);
        Assert.Equal(1, _navigation.PlayerLeaves);
        Assert.Equal(AppRoute.WatchParty, Assert.Single(_navigation.Sections));
    }

    [Fact]
    public async Task ARoomThatMovedOnBeforeThePlayerBound_LeavesAtOnce_BackToTheRoom()
    {
        var viewModel = await JoinedAsync(WatchPartyPayloads.Room("playing", "playing", generation: 5, revision: 2, role: "guest", selected: "movie:heat"));

        await viewModel.StartAsync(PlayRequest.WatchParty("movie:heat", "77", 1));

        Assert.True(viewModel.HasLeft);
        Assert.Null(Playback.Started);
        Assert.Equal(1, _navigation.PlayerLeaves);
        Assert.Empty(_navigation.Sections);
    }

    [Fact]
    public async Task ATakeoverDuringPlayback_LeavesThePlayer_ForTheHub()
    {
        await PlayingAsync(role: "guest");

        Socket.Push("""{ "type": "connection_replaced", "reason": "This profile joined the Watch Party on another device." }""");
        await ServerPlaybackFixture.Until(() => _navigation.PlayerLeaves == 1, "the player leaving");

        Assert.Equal(1, Playback.Stops);
        Assert.Equal(AppRoute.WatchParty, Assert.Single(_navigation.Sections));
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
        Assert.Equal(AppRoute.WatchParty, Assert.Single(_navigation.Sections));
        Assert.Equal(1, Playback.Stops);
        Assert.Equal("host_left", _party.End?.Reason);
        await Marked();
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

    [Fact]
    public async Task InARoom_TheTaskbarsPlayPause_AsksTheRoom()
    {
        var viewModel = await PlayingAsync();
        var toolbar = new FakeTaskbarToolbar();
        using var taskbar = new PlayerTaskbarToolbar(viewModel, toolbar, new ImmediateDispatcher());

        Assert.Equal(new TaskbarToolbarState(TaskbarButtonState.Hidden, TaskbarButtonState.Enabled, TaskbarButtonState.Hidden, IsPaused: false),
            toolbar.Current);

        toolbar.Press(TaskbarButton.PlayPause);

        Assert.False(_engine.Current.IsPaused);
        await ServerPlaybackFixture.Until(() => Socket.Sent.Any(f => f.Contains("\"action\":\"pause\"", StringComparison.Ordinal)), "the request");
    }

    [Fact]
    public async Task AGuestTheRoomDoesNotLetPause_HasTheTaskbarsPlayPauseOff_AndAPressAsksNothing()
    {
        var viewModel = await PlayingAsync(role: "guest", transport: false);
        var toolbar = new FakeTaskbarToolbar();
        using var taskbar = new PlayerTaskbarToolbar(viewModel, toolbar, new ImmediateDispatcher());

        Assert.Equal(TaskbarButtonState.Disabled, toolbar.Current!.PlayPause);

        toolbar.Press(TaskbarButton.PlayPause);

        Assert.False(_engine.Current.IsPaused);
        Assert.Null(viewModel.Notice);
        Assert.DoesNotContain(Socket.Sent, f => f.Contains("\"transport_request\"", StringComparison.Ordinal));
    }

    // The room plays an episode of a series, with the taskbar's buttons on the toolbar given following the player from
    // before its start.
    private async Task<PlayerTaskbarToolbar> EpisodeInARoomAsync(FakeTaskbarToolbar toolbar, string role, bool transport = true)
    {
        var viewModel = await JoinedAsync(WatchPartyPayloads.Room("playing", "playing", generation: 3, revision: 1, role: role,
            selected: "episode:dark-1-3", transport: transport));
        var taskbar = new PlayerTaskbarToolbar(viewModel, toolbar, new ImmediateDispatcher());
        await viewModel.StartAsync(PlayRequest.WatchParty("episode:dark-1-3", "77", 1));
        Playback.Episode = new PlayingEpisode("episode:dark-1-3", "series:dark", "Dark", 1, 3);
        Playback.MediaInfo = new PlaybackMediaInfo("Secrets", "Dark — S1 · E3", null) { Kind = PlaybackMediaKind.Episode };
        Playback.Raise(new ServerPlaybackStatus(ServerPlaybackPhase.Playing));
        _engine.RaiseState(new PlaybackSnapshot(true, false, TimeSpan.FromSeconds(30), TimeSpan.FromHours(2), null, HasStarted: true));
        return taskbar;
    }

    [Fact]
    public async Task AnEpisodeInARoom_HasTheTaskbarsPreviousAndNextGreyed_ForAGuestWithoutPermission_AndTheirPressesDoNothing()
    {
        var toolbar = new FakeTaskbarToolbar();
        using var taskbar = await EpisodeInARoomAsync(toolbar, "guest", transport: false);

        Assert.Equal(new TaskbarToolbarState(TaskbarButtonState.Disabled, TaskbarButtonState.Disabled, TaskbarButtonState.Disabled, IsPaused: false),
            toolbar.Current);

        toolbar.Press(TaskbarButton.Previous);
        toolbar.Press(TaskbarButton.PlayPause);
        toolbar.Press(TaskbarButton.Next);

        Assert.Single(_playbacks);
        Assert.False(_engine.Current.IsPaused);
        Assert.DoesNotContain(Socket.Sent, f => f.Contains("\"transport_request\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEpisodeInARoom_HasTheTaskbarsPreviousAndNextGreyed_ForTheHostToo_WhosePlayPauseAsksTheRoom()
    {
        var toolbar = new FakeTaskbarToolbar();
        using var taskbar = await EpisodeInARoomAsync(toolbar, "host");

        Assert.Equal(new TaskbarToolbarState(TaskbarButtonState.Disabled, TaskbarButtonState.Enabled, TaskbarButtonState.Disabled, IsPaused: false),
            toolbar.Current);

        toolbar.Press(TaskbarButton.Previous);
        toolbar.Press(TaskbarButton.Next);
        Assert.Single(_playbacks);

        toolbar.Press(TaskbarButton.PlayPause);
        Assert.False(_engine.Current.IsPaused);
        await ServerPlaybackFixture.Until(() => Socket.Sent.Any(f => f.Contains("\"action\":\"pause\"", StringComparison.Ordinal)), "the request");
    }

    [Fact]
    public async Task TheRoomTakingAGuestsPauseAway_TurnsTheTaskbarsPlayPauseOff_AndAPressFromBeforeAsksNothing()
    {
        var viewModel = await PlayingAsync(role: "guest");
        var toolbar = new FakeTaskbarToolbar();
        var dispatcher = new QueuedDispatcher();
        using var taskbar = new PlayerTaskbarToolbar(viewModel, toolbar, dispatcher);
        Assert.Equal(TaskbarButtonState.Enabled, toolbar.Current!.PlayPause);
        toolbar.Press(TaskbarButton.PlayPause);

        Socket.Push(WatchPartyPayloads.Snapshot(WatchPartyPayloads.Room("playing", "playing", generation: 4, revision: 1, role: "guest",
            selected: "movie:heat", transport: false)));
        await ServerPlaybackFixture.Until(() => toolbar.Current is { PlayPause: TaskbarButtonState.Disabled }, "the button turned off");
        dispatcher.RunAll();

        Assert.False(_engine.Current.IsPaused);
        Assert.DoesNotContain(Socket.Sent, f => f.Contains("\"transport_request\"", StringComparison.Ordinal));
    }

    public async ValueTask DisposeAsync() => await _party.LeaveAsync();
}
