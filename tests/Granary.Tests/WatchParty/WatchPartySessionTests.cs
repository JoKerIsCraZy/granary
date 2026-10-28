using System.Net;
using Granary.Core.Api.WatchParty;
using Granary.Core.Auth;
using Granary.Core.WatchParty;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.WatchParty;

/// <summary>The Watch Party this profile is in: going in, following the room, leaving, and another device taking over.</summary>
public sealed class WatchPartySessionTests : IAsyncDisposable
{
    private const string Room = "api/v2/watch-together/rooms/" + WatchPartyPayloads.RoomId;

    private readonly ScriptedSilo _silo = new();
    private readonly FakeSessionManager _session = new()
    {
        State = SessionState.Ready,
        ActiveServer = ScriptedSilo.Server,
        ProfileId = "7",
    };
    private readonly LogSink _logs = new();
    private readonly List<FakeRealtimeSocket> _sockets = [];
    private readonly WatchPartySession _party;

    public WatchPartySessionTests()
    {
        _silo.On(HttpMethod.Post, Room + "/ws-ticket", ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.TicketAnswer));
        _silo.On(HttpMethod.Post, "api/v2/watch-together/rooms", ScriptedSilo.Json(HttpStatusCode.Created, WatchPartyPayloads.Answer(WatchPartyPayloads.Room())));
        _silo.On(HttpMethod.Post, "api/v2/watch-together/join",
            ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.Answer(WatchPartyPayloads.Room(role: "guest"))));
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

    private FakeRealtimeSocket Socket(int index)
    {
        lock (_sockets)
        {
            return _sockets[index];
        }
    }

    private Task Connected(int count = 1) => ServerPlaybackFixture.Until(() => _sockets.Count == count && _party.IsConnected, "the room connection");

    [Fact]
    public async Task Creating_GoesIntoTheRoom_AndConnects()
    {
        var room = await _party.CreateAsync(TestContext.Current.CancellationToken);
        await Connected();

        Assert.Equal("ABCD1234", room.Code);
        Assert.True(_party.IsInRoom);
        Assert.Same(room, _party.Room);
    }

    [Fact]
    public async Task Joining_TakesACode_AndSnapshotsFollow_OldOnesDoNot()
    {
        await _party.JoinAsync("abcd1234", TestContext.Current.CancellationToken);
        await Connected();

        Socket(0).Push(WatchPartyPayloads.Snapshot(WatchPartyPayloads.Room(generation: 5, role: "guest", selected: "movie:heat")));
        await ServerPlaybackFixture.Until(() => _party.Room!.Generation == 5, "the snapshot");
        Socket(0).Push(WatchPartyPayloads.Snapshot(WatchPartyPayloads.Room(generation: 3, role: "guest")));
        Socket(0).Push(WatchPartyPayloads.Snapshot(WatchPartyPayloads.Room(generation: 6, role: "guest", selected: "movie:heat")));
        await ServerPlaybackFixture.Until(() => _party.Room!.Generation == 6, "the newer snapshot");

        Assert.True(_party.Room!.IsStaged);
        Assert.Equal("""{"code":"ABCD1234"}""", _silo.SentTo(HttpMethod.Post, "api/v2/watch-together/join")[0].Body);
    }

    [Fact]
    public async Task Commands_ComeWithTheirConnection()
    {
        var deliveries = new List<WatchPartyDelivery>();
        _party.CommandReceived += (_, delivery) => deliveries.Add(delivery);
        await _party.CreateAsync(TestContext.Current.CancellationToken);
        await Connected();

        Socket(0).Push(WatchPartyPayloads.Command("c1", "play", 10, "2026-10-01T12:00:00.000Z"));
        await ServerPlaybackFixture.Until(() => deliveries.Count == 1, "the command");

        Assert.Equal(("c1", _party.ConnectionEpoch), (deliveries[0].Command.CommandId, deliveries[0].Epoch));
    }

    [Fact]
    public async Task TheHost_StagesStartsAndStops_TheAnswersShow()
    {
        string staged = WatchPartyPayloads.Answer(WatchPartyPayloads.Room(generation: 2, selected: "movie:heat"));
        string playing = WatchPartyPayloads.Answer(WatchPartyPayloads.Room("playing", "waiting", generation: 3, revision: 1, selected: "movie:heat"));
        _silo.On(HttpMethod.Put, Room + "/staged-selection", ScriptedSilo.Json(HttpStatusCode.OK, staged));
        _silo.On(HttpMethod.Post, Room + "/playback/start", ScriptedSilo.Json(HttpStatusCode.OK, playing));
        await _party.CreateAsync(TestContext.Current.CancellationToken);

        await _party.StageAsync("movie:heat");
        Assert.True(_party.Room!.IsStaged);
        await _party.StartAsync();

        Assert.True(_party.Room!.IsPlaying);
        Assert.Equal(1, _party.Room.SelectionRevision);
    }

    [Fact]
    public async Task AnAnswerAfterTheLeave_DoesNotBringTheRoomBack()
    {
        var answered = new TaskCompletionSource();
        string playing = WatchPartyPayloads.Answer(WatchPartyPayloads.Room("playing", "waiting", generation: 3, revision: 1, selected: "movie:heat"));
        _silo.On(HttpMethod.Post, Room + "/playback/start", async () =>
        {
            await answered.Task;
            return await ScriptedSilo.Json(HttpStatusCode.OK, playing)();
        });
        await _party.CreateAsync(TestContext.Current.CancellationToken);
        await Connected();
        _party.StartedRevision = 1;

        var starting = _party.StartAsync();
        await _party.LeaveAsync();
        answered.SetResult();
        await starting;

        Assert.Null(_party.Room);
        Assert.False(_party.IsInRoom);
        Assert.Null(_party.StartedRevision);
        Assert.Single(_sockets);
    }

    [Fact]
    public async Task ARoomCreatedForTheProfileBefore_IsNotEntered()
    {
        var answered = new TaskCompletionSource();
        _silo.On(HttpMethod.Post, "api/v2/watch-together/rooms", async () =>
        {
            await answered.Task;
            return await ScriptedSilo.Json(HttpStatusCode.Created, WatchPartyPayloads.Answer(WatchPartyPayloads.Room()))();
        });

        var creating = _party.CreateAsync(TestContext.Current.CancellationToken);
        _session.ProfileId = "8";
        _session.RaiseChanged();
        answered.SetResult();

        await Assert.ThrowsAsync<WatchPartyStaleException>(() => creating);
        Assert.Null(_party.Room);
        Assert.Empty(_sockets);
    }

    [Fact]
    public async Task Leaving_ClosesTheSocket_AndForgetsTheRoom()
    {
        await _party.CreateAsync(TestContext.Current.CancellationToken);
        await Connected();

        await _party.LeaveAsync();

        Assert.False(_party.IsInRoom);
        Assert.Null(_party.Room);
        Assert.True(Socket(0).Disposed);
    }

    [Fact]
    public async Task TheHostsEnd_EndsTheRoom_ForEveryone()
    {
        _silo.On(HttpMethod.Delete, Room, ScriptedSilo.NoContent());
        await _party.CreateAsync(TestContext.Current.CancellationToken);

        await _party.EndRoomAsync();

        Assert.Single(_silo.SentTo(HttpMethod.Delete, Room));
        Assert.Null(_party.Room);
    }

    [Fact]
    public async Task AnotherDevice_TakesOver_AndRejoinClaimsItBack()
    {
        await _party.CreateAsync(TestContext.Current.CancellationToken);
        await Connected();

        Socket(0).Push("""{ "type": "connection_replaced", "reason": "This profile joined the Watch Party on another device." }""");
        await ServerPlaybackFixture.Until(() => _party.End is not null, "the takeover");
        Assert.True(_party.End!.Replaced);
        Assert.False(_party.IsInRoom);

        _party.Rejoin();
        await Connected(2);

        Assert.Null(_party.End);
    }

    [Fact]
    public async Task AnotherProfile_LeavesTheRoom()
    {
        await _party.CreateAsync(TestContext.Current.CancellationToken);
        await Connected();

        _session.ProfileId = "8";
        _session.RaiseChanged();
        await ServerPlaybackFixture.Until(() => _party.Room is null && Socket(0).Disposed, "the leave");

        Assert.False(_party.IsInRoom);
    }

    public async ValueTask DisposeAsync() => await _party.LeaveAsync();
}
