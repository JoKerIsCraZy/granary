using System.Net;
using Granary.Core.Abstractions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels.WatchParty;
using Granary.Core.WatchParty;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.WatchParty;

/// <summary>The hub, the room page and the sidebar's gate, in the web's words.</summary>
public sealed class WatchPartyPagesTests : IAsyncDisposable
{
    private const string Room = "api/v2/watch-together/rooms/" + WatchPartyPayloads.RoomId;
    private const string Capabilities = "api/v2/watch-together/capabilities";

    private readonly ScriptedSilo _silo = new();
    private readonly FakeSessionManager _session = new() { State = SessionState.Ready, ActiveServer = ScriptedSilo.Server, ProfileId = "7" };
    private readonly LogSink _logs = new();
    private readonly List<FakeRealtimeSocket> _sockets = [];
    private readonly RecordingNavigation _navigation = new();
    private readonly ManualTimeProvider _time = new();
    private readonly WatchPartySession _party;
    private readonly WatchPartyApi _api;

    public WatchPartyPagesTests()
    {
        _silo.On(HttpMethod.Post, Room + "/ws-ticket", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.TicketAnswer));
        _silo.On(HttpMethod.Get, Capabilities, ScriptedSilo.Json(HttpStatusCode.OK,
            """{ "state": "available", "allowed": true, "socket_protocol": "silo.room.v2", "lobby_ready": true, "stop_playback": true }"""));
        _api = new WatchPartyApi(_silo.Http);
        _party = new WatchPartySession(_session, _api, () => new WatchPartyConnection(_api, () =>
        {
            var socket = new FakeRealtimeSocket { Picks = WatchPartyApi.Protocol };
            lock (_sockets)
            {
                _sockets.Add(socket);
            }
            return socket;
        }, TimeProvider.System, _logs.For<WatchPartyConnection>()), new ImmediateDispatcher(), _logs.For<WatchPartySession>());
    }

    private WatchPartyHubViewModel Hub() => new(_party, _navigation, _logs.For<WatchPartyHubViewModel>());

    private WatchPartyFeature Feature() => new(_session, _api, _time, _logs.For<WatchPartyFeature>());

    private WatchPartyRoomViewModel RoomPage(WatchPartyFeature feature) => new(_party, feature, _session, new CatalogApi(_silo.Http),
        new ImageParameterCache(new ImagesApi(_silo.Http), _logs.For<ImageParameterCache>()), _navigation, new ImmediateDispatcher(), _time,
        _logs.For<WatchPartyRoomViewModel>());

    private async Task JoinAsync(string room)
    {
        _silo.On(HttpMethod.Post, "api/v2/watch-together/join", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(room)));
        await _party.JoinAsync("ABCD1234", TestContext.Current.CancellationToken);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && _party.IsConnected, "the room connection");
    }

    [Fact]
    public async Task TheGate_OpensForAServedRoomSocket_Only()
    {
        var feature = Feature();
        await feature.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(feature.IsAvailable);

        _silo.On(HttpMethod.Get, Capabilities, ScriptedSilo.Json(HttpStatusCode.OK, """{ "state": "not_configured", "allowed": true, "socket_protocol": "" }"""));
        var other = Feature();
        await other.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(other.IsAvailable);
    }

    [Fact]
    public async Task Joining_FromTheHub_OpensTheRoom_AndAMissingOneSaysSo()
    {
        var hub = Hub();
        _silo.On(HttpMethod.Post, "api/v2/watch-together/join", ScriptedSilo.Problem(404, "not_found"));
        hub.Code = "WXYZ9876";

        await hub.JoinCommand.ExecuteAsync(null);
        Assert.Equal("Room not found.", hub.Error);

        _silo.On(HttpMethod.Post, "api/v2/watch-together/join", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(WatchPartyPayloads.Room(role: "guest"))));
        await hub.JoinCommand.ExecuteAsync(null);

        Assert.Equal(AppRoute.WatchPartyRoom, _navigation.Navigations[^1].Route);
        Assert.True(hub.IsInRoom);
        Assert.Equal("You're in Watch Party ABCD1234.", hub.CurrentRoomText);
    }

    [Fact]
    public async Task AJoinAnsweredAfterTheProfileChanged_ShowsNothing()
    {
        var hub = Hub();
        var answered = new TaskCompletionSource();
        _silo.On(HttpMethod.Post, "api/v2/watch-together/join", async () =>
        {
            await answered.Task;
            return await ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(WatchPartyPayloads.Room(role: "guest")))();
        });
        hub.Code = "ABCD1234";

        var joining = hub.JoinCommand.ExecuteAsync(null);
        _session.ProfileId = "8";
        _session.RaiseChanged();
        answered.SetResult();
        await joining;

        Assert.Equal("", hub.Error);
        Assert.False(hub.IsInRoom);
        Assert.DoesNotContain(_navigation.Navigations, n => n.Route == AppRoute.WatchPartyRoom);
    }

    [Fact]
    public async Task ATitleUpNext_CountsTheReadyGuests()
    {
        await JoinAsync(WatchPartyPayloads.Room(selected: "movie:heat"));
        var feature = Feature();
        await feature.RefreshAsync(TestContext.Current.CancellationToken);
        var page = RoomPage(feature);

        page.Activate();

        Assert.True(page.IsStaged);
        Assert.Equal("1 of 1 ready", page.StagedCaption);
        Assert.Equal("Start for everyone · 1/1 ready", page.StartLabel);
        Assert.Equal(["Elena", "Maya"], page.Members.Select(m => m.Name));
        Assert.Equal("Ready", page.Members[1].State);
        page.Deactivate();
    }

    [Fact]
    public async Task AStartedTitle_OpensThePlayer_OncePerStart()
    {
        await JoinAsync(WatchPartyPayloads.Room("playing", "waiting", generation: 2, revision: 1, role: "guest", selected: "movie:heat"));
        var page = RoomPage(Feature());

        page.Activate();
        _sockets[0].Push(WatchPartyPayloads.Snapshot(WatchPartyPayloads.Room("playing", "playing", generation: 3, revision: 1, role: "guest", selected: "movie:heat")));
        await ServerPlaybackFixture.Until(() => _party.Room!.Generation == 3, "the snapshot");

        var opened = Assert.Single(_navigation.Navigations, n => n.Route == AppRoute.Player);
        var request = Assert.IsType<PlayRequest>(opened.Parameter);
        Assert.Equal(("movie:heat", "77", 1L), (request.ContentId, request.PinnedFileId, request.SelectionRevision));
        Assert.True(page.IsPlaying);
        page.Deactivate();
    }

    [Fact]
    public async Task TheRoomPageShownAgain_DoesNotReopenThePlayer_ForTheSameStart()
    {
        await JoinAsync(WatchPartyPayloads.Room("playing", "playing", generation: 3, revision: 1, role: "guest", selected: "movie:heat"));
        var page = RoomPage(Feature());
        page.Activate();
        Assert.Single(_navigation.Navigations, n => n.Route == AppRoute.Player);
        page.Deactivate();

        // The viewer left the player: the room page comes up again, a new one (it is not cached).
        var again = RoomPage(Feature());
        again.Activate();
        Assert.Single(_navigation.Navigations, n => n.Route == AppRoute.Player);

        _sockets[0].Push(WatchPartyPayloads.Snapshot(WatchPartyPayloads.Room("playing", "waiting", generation: 5, revision: 2, role: "guest", selected: "movie:heat")));
        await ServerPlaybackFixture.Until(() => _navigation.Navigations.Count(n => n.Route == AppRoute.Player) == 2, "the next start");

        Assert.Equal(2, _party.StartedRevision);
        again.Deactivate();
    }

    [Fact]
    public async Task TheRoomsEnd_ReplacesThePage_InTheWebsWords()
    {
        await JoinAsync(WatchPartyPayloads.Room());
        var page = RoomPage(Feature());
        page.Activate();

        _sockets[0].Push("""{ "type": "room_closed", "reason": "host_left" }""");
        await ServerPlaybackFixture.Until(() => page.IsEnded, "the end");

        Assert.True(page.IsClosed);
        Assert.Equal("The room has ended.", page.EndedText);
        page.Deactivate();
    }

    public async ValueTask DisposeAsync() => await _party.LeaveAsync();
}
