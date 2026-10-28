using System.Net;
using System.Text.Json;
using Granary.Core.Api.WatchParty;
using Granary.Core.WatchParty;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.WatchParty;

/// <summary>The room socket, as the web keeps it: a ticket per connection, pings for the clock, snapshots and commands.</summary>
public sealed class WatchPartyConnectionTests
{
    private const string TicketPath = "api/v2/watch-together/rooms/" + WatchPartyPayloads.RoomId + "/ws-ticket";

    private readonly ScriptedSilo _silo = new();
    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();
    private readonly List<FakeRealtimeSocket> _sockets = [];
    private readonly List<WatchPartyFrame> _frames = [];
    private readonly List<WatchPartyEnd> _ends = [];

    public WatchPartyConnectionTests()
    {
        _silo.On(HttpMethod.Post, TicketPath, ScriptedSilo.Json(HttpStatusCode.OK, WatchPartyPayloads.TicketAnswer));
    }

    private WatchPartyConnection Open(bool open = true)
    {
        var connection = new WatchPartyConnection(new WatchPartyApi(_silo.Http), () =>
        {
            var socket = new FakeRealtimeSocket { Picks = WatchPartyApi.Protocol };
            lock (_sockets)
            {
                _sockets.Add(socket);
            }
            return socket;
        }, _time, _logs.For<WatchPartyConnection>());
        connection.FrameReceived += (_, frame) =>
        {
            lock (_frames)
            {
                _frames.Add(frame);
            }
        };
        connection.Ended += (_, end) => _ends.Add(end);
        if (open)
        {
            connection.Open(ScriptedSilo.Base, WatchPartyPayloads.RoomId, WatchPartyPayloads.Token);
        }
        return connection;
    }

    private FakeRealtimeSocket Socket(int index)
    {
        lock (_sockets)
        {
            return _sockets[index];
        }
    }

    private Task Until(Func<bool> condition, string what) => ServerPlaybackFixture.Until(condition, what);

    [Fact]
    public async Task Opening_OffersTheProtocolAndTicket_AndPingsForTheClock()
    {
        await using var connection = Open();
        await Until(() => _sockets.Count == 1 && !Socket(0).Sent.IsEmpty, "the first ping");

        Assert.Equal(WatchPartyPayloads.Token, Assert.Single(_silo.Sent).RoomToken);
        Assert.Equal(new Uri($"wss://nas.example/silo/api/v2/watch-together/rooms/{WatchPartyPayloads.RoomId}/ws"), Socket(0).Uri);
        Assert.Equal(["silo.room.v2", "silo.ticket." + WatchPartyPayloads.Ticket], Socket(0).Offered);
        var ping = JsonDocument.Parse(Socket(0).Sent.First()).RootElement;
        Assert.Equal("ping", ping.GetProperty("type").GetString());
        Assert.Equal(WatchPartyConnectionState.Connected, connection.State);
        Assert.Equal(1, connection.Epoch);
    }

    [Fact]
    public async Task SnapshotsAndCommands_ArePassedOn_PongsSetTheClock()
    {
        await using var connection = Open();
        await Until(() => _sockets.Count == 1 && connection.State == WatchPartyConnectionState.Connected, "the connection");

        Socket(0).Push(WatchPartyPayloads.Snapshot(WatchPartyPayloads.Room(generation: 4)));
        Socket(0).Push(WatchPartyPayloads.Command("c1", "seek", 61.5, "2026-10-01T12:00:02.000Z", state: "waiting"));
        // The server's clock is 10 s ahead; the round trip took nothing on the manual clock.
        Socket(0).Push("""{ "type": "pong", "client_sent_at": "2026-10-01T12:00:00.000Z", "server_received_at": "2026-10-01T12:00:10.000Z", "server_sent_at": "2026-10-01T12:00:10.000Z" }""");
        Socket(0).Push("""{ "type": "suggestions_update", "suggestions": [] }""");
        await Until(() => connection.Clock.OffsetMs is not null && _frames.Count == 2, "the frames");

        Assert.Equal(4, Assert.IsType<WatchPartyFrame.Snapshot>(_frames[0]).Room.Generation);
        var command = Assert.IsType<WatchPartyFrame.Command>(_frames[1]).Value;
        Assert.Equal(("c1", "seek", 61.5, "waiting"), (command.CommandId, command.Action, command.PositionSeconds, command.PlaybackState));
        Assert.Equal(10_000, connection.Clock.OffsetMs);
    }

    [Fact]
    public async Task ALostConnection_ComesBack_WithAFreshTicket()
    {
        await using var connection = Open();
        await Until(() => _sockets.Count == 1 && connection.State == WatchPartyConnectionState.Connected, "the connection");

        Socket(0).Close();
        await Until(() => connection.State == WatchPartyConnectionState.Disconnected, "the loss");
        _time.Advance(TimeSpan.FromMilliseconds(500));
        await Until(() => _sockets.Count == 2 && connection.State == WatchPartyConnectionState.Connected, "the second connection");

        Assert.Equal(2, _silo.SentTo(HttpMethod.Post, TicketPath).Count);
        Assert.Equal(2, connection.Epoch);
        Assert.Empty(_ends);
    }

    [Fact]
    public async Task ARoomThatClosed_EndsTheEngagement()
    {
        await using var connection = Open();
        await Until(() => _sockets.Count == 1 && connection.State == WatchPartyConnectionState.Connected, "the connection");

        Socket(0).Push("""{ "type": "room_closed", "reason": "host_left" }""");
        await Until(() => _ends.Count == 1, "the end");

        Assert.Equal(new WatchPartyEnd("host_left"), _ends[0]);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Single(_sockets);
    }

    [Fact]
    public async Task AnotherDevice_TakesTheMembershipOver()
    {
        await using var connection = Open();
        await Until(() => _sockets.Count == 1 && connection.State == WatchPartyConnectionState.Connected, "the connection");

        Socket(0).Push("""{ "type": "connection_replaced", "reason": "This profile joined the Watch Party on another device." }""");
        await Until(() => _ends.Count == 1, "the end");

        Assert.True(_ends[0].Replaced);
        Assert.Equal("This profile joined the Watch Party on another device.", _ends[0].Message);
    }

    [Theory]
    [InlineData(409, "ended")]
    [InlineData(404, "not_found")]
    [InlineData(403, "forbidden")]
    public async Task ARefusedTicket_EndsIt(int status, string reason)
    {
        _silo.On(HttpMethod.Post, TicketPath, ScriptedSilo.Problem(status, "refused"));

        await using var connection = Open();
        await Until(() => _ends.Count == 1, "the end");

        Assert.Equal(reason, _ends[0].Reason);
        Assert.Empty(_sockets);
    }

    [Fact]
    public async Task Sending_GoesOnTheOpenConnection_AndFailsWithoutOne()
    {
        await using var connection = Open(open: false);
        Assert.False(await connection.SendAsync(WatchPartyFrames.LobbyReady(true)));
        connection.Open(ScriptedSilo.Base, WatchPartyPayloads.RoomId, WatchPartyPayloads.Token);
        await Until(() => _sockets.Count == 1 && connection.State == WatchPartyConnectionState.Connected, "the connection");

        Assert.True(await connection.SendAsync(WatchPartyFrames.LobbyReady(true)));

        Assert.Contains("""{"type":"lobby_ready","ready":true}""", Socket(0).Sent);
    }

    [Fact]
    public void TheClock_KeepsTheShortestRoundTrip_AndDropsSamplesAfterAStep()
    {
        var clock = new RoomClock();
        var t0 = DateTimeOffset.Parse("2026-10-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        clock.Record(t0, t0.AddMilliseconds(5_100), t0.AddMilliseconds(5_100), t0.AddMilliseconds(400));
        clock.Record(t0, t0.AddMilliseconds(5_020), t0.AddMilliseconds(5_020), t0.AddMilliseconds(40));
        Assert.Equal(5_000, clock.OffsetMs);
        Assert.False(clock.Record(t0, t0, t0, t0.AddMilliseconds(6_000)));

        // A clock that stepped by a minute cannot agree with the samples before.
        clock.Record(t0, t0.AddMilliseconds(65_100), t0.AddMilliseconds(65_100), t0.AddMilliseconds(200));
        Assert.Equal(65_000, clock.OffsetMs);
    }

    [Fact]
    public void Frames_AreWrittenAsTheServerReadsThem()
    {
        Assert.Equal("""{"type":"attach_session","session_id":"s1"}""", WatchPartyFrames.Attach("s1"));
        Assert.Equal("""{"type":"state_report","session_id":"s1","command_id":"c1","position_seconds":12.346,"is_paused":false,"is_ready":true}""",
            WatchPartyFrames.StateReport("s1", "c1", 12.34567, isPaused: false, isReady: true));
        Assert.Equal("""{"type":"state_report","session_id":"s1","position_seconds":0,"is_paused":true}""",
            WatchPartyFrames.StateReport("s1", null, -3, isPaused: true));
        Assert.Equal("""{"type":"transport_request","action":"seek","position_seconds":90,"is_paused":true}""",
            WatchPartyFrames.TransportRequest("seek", 90, isPaused: true));
        Assert.Equal("""{"type":"buffering","session_id":"s1","position_seconds":5,"is_paused":false}""", WatchPartyFrames.Buffering("s1", 5, false));
        Assert.Null(WatchPartyFrames.Parse("""{ "type": "transport_command", "command": { "command_id": "c", "action": "rewind" } }"""));
        Assert.Null(WatchPartyFrames.Parse("not json"));
    }
}
