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

    // Loses the connection of socket <paramref name="index"/>; answers how long it waits before the next.
    private async Task<TimeSpan> LoseAsync(WatchPartyConnection connection, int index)
    {
        Socket(index).Close();
        await _time.WaitUntilAsync(() => connection.State == WatchPartyConnectionState.Disconnected
            && _time.NextDue is { } due && due <= TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);
        return _time.NextDue!.Value;
    }

    private async Task ComeBackAsync(WatchPartyConnection connection, TimeSpan wait, int sockets)
    {
        _time.Advance(wait);
        await Until(() => _sockets.Count == sockets && connection.State == WatchPartyConnectionState.Connected, "the next connection");
    }

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
        // The server's clock is 10 s ahead; the round trip took nothing on the manual clock. Three samples make a clock.
        for (int i = 0; i < RoomClock.MinSamples; i++)
        {
            Socket(0).Push("""{ "type": "pong", "client_sent_at": "2026-10-01T12:00:00.000Z", "server_received_at": "2026-10-01T12:00:10.000Z", "server_sent_at": "2026-10-01T12:00:10.000Z" }""");
        }
        Socket(0).Push("""{ "type": "reaction", "emoji": "x" }""");
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

        var wait = await LoseAsync(connection, 0);
        Assert.InRange(wait, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(600));
        await ComeBackAsync(connection, wait, 2);

        Assert.Equal(2, _silo.SentTo(HttpMethod.Post, TicketPath).Count);
        Assert.Equal(2, connection.Epoch);
        Assert.Empty(_ends);
    }

    [Fact]
    public async Task TheWaits_StartOver_OnlyAfterAConnectionHeld()
    {
        await using var connection = Open();
        await Until(() => _sockets.Count == 1 && connection.State == WatchPartyConnectionState.Connected, "the connection");

        var first = await LoseAsync(connection, 0);
        await ComeBackAsync(connection, first, 2);
        // Lost again at once: a connection that opened is no reason to hurry back.
        var second = await LoseAsync(connection, 1);
        Assert.InRange(second, TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(1200));
        await ComeBackAsync(connection, second, 3);

        _time.Advance(WatchPartyConnection.SteadyAfter);
        var afterSteady = await LoseAsync(connection, 2);

        Assert.InRange(afterSteady, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(600));
    }

    [Fact]
    public async Task TheFiveMinutes_RenewTheConnectionAtOnce_WithoutReconnecting()
    {
        await using var connection = Open();
        await Until(() => _sockets.Count == 1 && connection.State == WatchPartyConnectionState.Connected, "the connection");
        var states = new List<WatchPartyConnectionState>();
        connection.StateChanged += (_, state) =>
        {
            lock (states)
            {
                states.Add(state);
            }
        };

        _time.Advance(TimeSpan.FromSeconds(300));
        await Until(() => _sockets.Count == 2 && connection.Epoch == 2, "the renewed connection");
        // The server's own close a moment before the five minutes are up on this clock renews it as well.
        _time.Advance(TimeSpan.FromSeconds(297));
        Socket(1).Close();
        await Until(() => _sockets.Count == 3 && connection.Epoch == 3, "the connection after the server's close");

        Assert.True(Socket(0).Disposed);
        Assert.Equal(3, _silo.SentTo(HttpMethod.Post, TicketPath).Count);
        Assert.Empty(states);
        Assert.Equal(WatchPartyConnectionState.Connected, connection.State);
    }

    [Theory]
    [InlineData("host_left", "host_left")]
    [InlineData("ended", "ended")]
    [InlineData("Bearer abc.def", "other")]
    [InlineData("token=secret", "other")]
    [InlineData("", "other")]
    public void AnEndsReason_IsLoggedOnlyAsACode(string reason, string logged) => Assert.Equal(logged, new WatchPartyEnd(reason).LoggedReason);

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
    public void TheClock_IsTheMedianOfTheShortestRoundTrips_FromThreeSamplesOn()
    {
        var clock = new RoomClock();
        var t0 = DateTimeOffset.Parse("2026-10-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        // Offsets 4900 (400 ms there and back), 5000 (40 ms), 5005 (50 ms).
        clock.Record(t0, t0.AddMilliseconds(5_100), t0.AddMilliseconds(5_100), t0.AddMilliseconds(400));
        clock.Record(t0, t0.AddMilliseconds(5_020), t0.AddMilliseconds(5_020), t0.AddMilliseconds(40));
        Assert.Null(clock.OffsetMs);
        clock.Record(t0, t0.AddMilliseconds(5_030), t0.AddMilliseconds(5_030), t0.AddMilliseconds(50));
        Assert.Equal(5_000, clock.OffsetMs);
        // 5010 in 30 ms: the three shortest are 5010, 5000 and 5005.
        clock.Record(t0, t0.AddMilliseconds(5_025), t0.AddMilliseconds(5_025), t0.AddMilliseconds(30));
        Assert.Equal(5_005, clock.OffsetMs);

        Assert.False(clock.Record(t0, t0, t0, t0.AddMilliseconds(6_000)));
        Assert.False(clock.Record(t0, t0.AddHours(13), t0.AddHours(13), t0));
        Assert.Equal(5_005, clock.OffsetMs);
    }

    [Fact]
    public void AClockThatStepped_StartsTheSamplesOver()
    {
        var clock = new RoomClock();
        var t0 = DateTimeOffset.Parse("2026-10-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        for (int i = 0; i < RoomClock.MinSamples; i++)
        {
            clock.Record(t0, t0.AddMilliseconds(5_020), t0.AddMilliseconds(5_020), t0.AddMilliseconds(40));
        }

        // A minute later than any sample before could be.
        clock.Record(t0, t0.AddMilliseconds(65_100), t0.AddMilliseconds(65_100), t0.AddMilliseconds(200));
        Assert.Null(clock.OffsetMs);
        clock.Record(t0, t0.AddMilliseconds(65_100), t0.AddMilliseconds(65_100), t0.AddMilliseconds(200));
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
