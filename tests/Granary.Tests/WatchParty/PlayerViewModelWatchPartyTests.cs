using System.Net;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
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

    private async Task<PlayerViewModel> PlayingAsync(string role = "host")
    {
        string playing = WatchPartyPayloads.Room("playing", "playing", generation: 3, revision: 1, role: role, selected: "movie:heat");
        _silo.On(HttpMethod.Post, "api/v2/watch-together/join", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(playing)));
        await _party.JoinAsync("ABCD1234", TestContext.Current.CancellationToken);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && _party.IsConnected, "the room connection");
        var viewModel = new PlayerViewModel(_engine, new ImmediateDispatcher(), _navigation, () =>
        {
            var playback = new FakeServerPlayback();
            _playbacks.Add(playback);
            return playback;
        }, new PlaybackStops(), _time, watchParty: _party);
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
    public async Task AGuestsSeek_IsRefused_WithTheWebsWords()
    {
        var viewModel = await PlayingAsync(role: "guest");

        viewModel.SeekTo(600);

        Assert.Empty(Playback.Seeks);
        Assert.Equal(WatchPartyPlaybackBinding.HostSeeksText, viewModel.NoticeMessage);
        Assert.False(viewModel.CanSeekInRoom);
    }

    [Fact]
    public async Task TheRoomBackInItsLobby_ClosesThePlayer()
    {
        await PlayingAsync();

        Socket.Push(WatchPartyPayloads.Snapshot(WatchPartyPayloads.Room("lobby", "idle", generation: 4, revision: 2, selected: "movie:heat")));
        await ServerPlaybackFixture.Until(() => _navigation.PlayerLeaves == 1, "the player leaving");

        Assert.Equal(1, Playback.Stops);
    }

    public async ValueTask DisposeAsync() => await _party.LeaveAsync();
}
