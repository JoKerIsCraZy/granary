using System.Net;
using System.Text.Json;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Tests.Fakes;
using Granary.Tests.Profiles;

namespace Granary.Tests.Playback;

/// <summary>
/// The playback control lane, as the web player and Silo's Android app keep it: a ticket, the socket offering the
/// protocol and the ticket, hello, then ack, the player's work and the result for every command.
/// </summary>
public sealed class PlaybackControlLaneTests
{
    private const string Session = PlaybackPayloads.SessionId;
    private const string TicketPath = "api/v2/playback/sessions/" + Session + "/control/ws-ticket";
    private const string Ticket = """{ "ticket": "AbC-123_xyz", "expires_in": 30, "max_connection_seconds": 14400, "protocol": "silo.playback-control.v2" }""";

    private readonly ScriptedSilo _silo = new();
    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();
    private readonly List<FakeRealtimeSocket> _sockets = [];
    private readonly List<PlaybackControlCommand> _commands = [];
    private Func<PlaybackControlCommand, PlaybackControlReply> _reply = _ => PlaybackControlReply.Done;

    public PlaybackControlLaneTests()
    {
        _silo.On(HttpMethod.Post, TicketPath, ScriptedSilo.Json(HttpStatusCode.OK, Ticket));
    }

    private PlaybackControlLane Lane() => new(new PlaybackControlApi(_silo.Http), () =>
    {
        var socket = new FakeRealtimeSocket();
        lock (_sockets)
        {
            _sockets.Add(socket);
        }
        return socket;
    }, _time, _logs.For<PlaybackControlLane>());

    private FakeRealtimeSocket Socket(int index) => _sockets[index];

    private void Follow(PlaybackControlLane lane) => lane.Follow(ScriptedSilo.Base, Session, PlaybackPayloads.InstallationId, command =>
    {
        lock (_commands)
        {
            _commands.Add(command);
        }
        return Task.FromResult(_reply(command));
    });

    private static List<JsonElement> Frames(FakeRealtimeSocket socket) => [.. socket.Sent.Select(text => JsonDocument.Parse(text).RootElement)];

    [Fact]
    public async Task Opening_MintsATicket_OffersItAsAProtocol_AndSaysHello()
    {
        await using var lane = Lane();

        Follow(lane);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && Socket(0).Sent.Count == 1, "the hello");

        var mint = JsonDocument.Parse(Assert.Single(_silo.SentTo(HttpMethod.Post, TicketPath)).Body!).RootElement;
        Assert.Equal(PlaybackPayloads.InstallationId, mint.GetProperty("installation_id").GetString());
        Assert.Equal(new Uri($"wss://nas.example/silo/api/v2/playback/sessions/{Session}/control/ws"), Socket(0).Uri);
        Assert.Equal(["silo.playback-control.v2", "silo.ticket.AbC-123_xyz"], Socket(0).Offered);
        var hello = Frames(Socket(0))[0];
        Assert.Equal(("hello", Session, "granary"), (hello.GetProperty("type").GetString(), hello.GetProperty("session_id").GetString(),
            hello.GetProperty("client").GetProperty("name").GetString()));
        Assert.Equal(PlaybackControlCommands.Supported, hello.GetProperty("capabilities").GetProperty("commands").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task Command_IsAcked_CarriedOut_ThenAnswered_AndItsFollowUpRunsLast()
    {
        var order = new List<string>();
        _reply = _ => PlaybackControlReply.Done with { Then = () => order.Add("then:" + Socket(0).Sent.Count) };
        await using var lane = Lane();
        Follow(lane);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && Socket(0).Sent.Count == 1, "the hello");

        Socket(0).Push(FakeRealtimeSocket.Command(Session, "c1", "pause"));
        await ServerPlaybackFixture.Until(() => Socket(0).Sent.Count == 3 && order.Count == 1, "the answer");

        var frames = Frames(Socket(0));
        Assert.Equal(("ack", "c1", "accepted"), (frames[1].GetProperty("type").GetString(), frames[1].GetProperty("command_id").GetString(),
            frames[1].GetProperty("status").GetString()));
        Assert.Equal(("result", "c1", "completed"), (frames[2].GetProperty("type").GetString(), frames[2].GetProperty("command_id").GetString(),
            frames[2].GetProperty("status").GetString()));
        Assert.False(frames[2].TryGetProperty("error", out _));
        // The follow-up (a stop leaving the player) ran after the result went out.
        Assert.Equal(["then:3"], order);
        Assert.Equal("pause", Assert.Single(_commands).Name);
    }

    [Fact]
    public async Task RejectedCommand_SaysWhy_AndARepeatedOneIsDoneOnce()
    {
        _reply = command => command.Name == "play_media" ? PlaybackControlReply.Rejected("unsupported") : PlaybackControlReply.Done;
        await using var lane = Lane();
        Follow(lane);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && Socket(0).Sent.Count == 1, "the hello");

        Socket(0).Push(FakeRealtimeSocket.Command(Session, "c1", "play_media"));
        Socket(0).Push(FakeRealtimeSocket.Command(Session, "c1", "play_media"));
        Socket(0).Push(FakeRealtimeSocket.Command("another-session", "c2", "pause"));
        Socket(0).Push("""{ "type": "event", "session_id": "x", "name": "markers_updated", "payload": {} }""");
        Socket(0).Push("not json");
        Socket(0).Push(FakeRealtimeSocket.Command(Session, "c3", "pause"));
        await ServerPlaybackFixture.Until(() => Socket(0).Sent.Count == 5, "the answers");

        var result = Frames(Socket(0))[2];
        Assert.Equal(("rejected", "unsupported"), (result.GetProperty("status").GetString(), result.GetProperty("error").GetString()));
        Assert.Equal(["c1", "c3"], _commands.Select(c => c.CommandId));
    }

    [Fact]
    public async Task LostConnection_ComesBack_WithAFreshTicket()
    {
        await using var lane = Lane();
        Follow(lane);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && Socket(0).Sent.Count == 1, "the hello");

        Socket(0).Close();
        // The connection's own four-hour timer went with it: what is due is the wait before coming back.
        await ServerPlaybackFixture.Until(() => _time.NextDue is { } due && due <= TimeSpan.FromMilliseconds(500), "the wait before coming back");
        _time.Advance(TimeSpan.FromMilliseconds(500));
        await ServerPlaybackFixture.Until(() => _sockets.Count == 2 && Socket(1).Sent.Count == 1, "the second hello");

        Assert.Equal(2, _silo.SentTo(HttpMethod.Post, TicketPath).Count);
        Assert.True(Socket(0).Disposed);
    }

    [Fact]
    public async Task AdministratorsTerminate_EndsTheLaneForGood()
    {
        await using var lane = Lane();
        Follow(lane);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && Socket(0).Sent.Count == 1, "the hello");

        Socket(0).Push(FakeRealtimeSocket.Command(Session, "c1", "terminate"));

        await lane.Running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Single(_sockets);
        Assert.Equal(3, Socket(0).Sent.Count);
    }

    [Theory]
    [InlineData(404, "not_found")]
    [InlineData(503, "dependency_unavailable")]
    public async Task ServerWithoutTheHandshake_OrAnEndedSession_HasNoLane(int status, string code)
    {
        _silo.On(HttpMethod.Post, TicketPath, ScriptedSilo.Problem(status, code));
        await using var lane = Lane();

        Follow(lane);

        await lane.Running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Empty(_sockets);
        Assert.Single(_silo.SentTo(HttpMethod.Post, TicketPath));
    }

    [Fact]
    public async Task RefusedTicketOrProtocol_IsTriedAgainLater()
    {
        _silo.On(HttpMethod.Post, TicketPath, ScriptedSilo.Problem(403, "permission_denied"), ScriptedSilo.Json(HttpStatusCode.OK, Ticket));
        await using var lane = Lane();

        Follow(lane);
        await ServerPlaybackFixture.Until(() => _time.NextDue is not null, "the wait after the refusal");
        _time.Advance(TimeSpan.FromMilliseconds(500));
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && Socket(0).Sent.Count == 1, "the hello");

        Assert.Equal(2, _silo.SentTo(HttpMethod.Post, TicketPath).Count);
    }

    [Fact]
    public async Task AnotherProtocol_IsNoLane()
    {
        await using var lane = new PlaybackControlLane(new PlaybackControlApi(_silo.Http), () =>
        {
            var socket = new FakeRealtimeSocket { Picks = "silo.events.v2" };
            _sockets.Add(socket);
            return socket;
        }, _time, _logs.For<PlaybackControlLane>());

        Follow(lane);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && _time.NextDue is not null, "the refusal");

        Assert.Empty(Socket(0).Sent);
        Assert.True(Socket(0).Disposed);
    }

    [Fact]
    public async Task Stop_EndsTheConnection_AndTheTicketNeverShowsInTheLog()
    {
        var lane = Lane();
        Follow(lane);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && Socket(0).Sent.Count == 1, "the hello");

        await lane.StopAsync();

        Assert.True(Socket(0).Disposed);
        Assert.Null(lane.SessionId);
        Assert.DoesNotContain(_logs.Lines, line => line.Contains("AbC-123_xyz", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Follow_OfAnotherSession_ReplacesTheLane()
    {
        await using var lane = Lane();
        Follow(lane);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && Socket(0).Sent.Count == 1, "the hello");
        const string other = "22222222-2222-4222-8222-222222222222";
        _silo.On(HttpMethod.Post, "api/v2/playback/sessions/" + other + "/control/ws-ticket", ScriptedSilo.Json(HttpStatusCode.OK, Ticket));

        lane.Follow(ScriptedSilo.Base, other, PlaybackPayloads.InstallationId, _ => Task.FromResult(PlaybackControlReply.Done));
        await ServerPlaybackFixture.Until(() => _sockets.Count == 2 && Socket(1).Sent.Count == 1, "the new session's hello");

        Assert.True(Socket(0).Disposed);
        Assert.Equal(other, lane.SessionId);
        Assert.Contains(other, Socket(1).Uri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_DoesNotWaitForAPlayerThatNeverAnswers()
    {
        await using var lane = new PlaybackControlLane(new PlaybackControlApi(_silo.Http), () =>
        {
            var socket = new FakeRealtimeSocket();
            _sockets.Add(socket);
            return socket;
        }, _time, _logs.For<PlaybackControlLane>());
        lane.Follow(ScriptedSilo.Base, Session, PlaybackPayloads.InstallationId, _ => new TaskCompletionSource<PlaybackControlReply>().Task);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && Socket(0).Sent.Count == 1, "the hello");
        Socket(0).Push(FakeRealtimeSocket.Command(Session, "c1", "pause"));
        await ServerPlaybackFixture.Until(() => Socket(0).Sent.Count == 2, "the ack");

        await lane.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(Socket(0).Disposed);
    }

    [Fact]
    public async Task UnexpectedFailure_IsLogged_AndTheLaneTriesAgain()
    {
        int made = 0;
        await using var lane = new PlaybackControlLane(new PlaybackControlApi(_silo.Http), () =>
        {
            if (made++ == 0)
            {
                throw new InvalidOperationException("secret-AbC-123_xyz");
            }
            var socket = new FakeRealtimeSocket();
            _sockets.Add(socket);
            return socket;
        }, _time, _logs.For<PlaybackControlLane>());

        Follow(lane);
        await ServerPlaybackFixture.Until(() => _time.NextDue is { } due && due <= TimeSpan.FromMilliseconds(500), "the wait after the failure");
        _time.Advance(TimeSpan.FromMilliseconds(500));
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && Socket(0).Sent.Count == 1, "the hello");

        Assert.Contains(_logs.Lines, line => line.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.DoesNotContain(_logs.Lines, line => line.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConnectionsTheServerDropsAtOnce_ComeBackEverMoreSlowly()
    {
        await using var lane = Lane();
        Follow(lane);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && Socket(0).Sent.Count == 1, "the hello");

        Socket(0).Close();
        await ServerPlaybackFixture.Until(() => _time.NextDue is { } due && due <= TimeSpan.FromMilliseconds(500), "the first wait");
        _time.Advance(TimeSpan.FromMilliseconds(500));
        await ServerPlaybackFixture.Until(() => _sockets.Count == 2 && Socket(1).Sent.Count == 1, "the second hello");
        Socket(1).Close();

        // The second connection held no time at all: the wait grows rather than starting over.
        await ServerPlaybackFixture.Until(() => _time.NextDue is { } due && due <= TimeSpan.FromSeconds(1), "the second wait");
        Assert.Equal(TimeSpan.FromSeconds(1), _time.NextDue);
    }

    [Fact]
    public async Task ConnectionThatHeld_StartsTheWaitsOver()
    {
        await using var lane = Lane();
        Follow(lane);
        await ServerPlaybackFixture.Until(() => _sockets.Count == 1 && Socket(0).Sent.Count == 1, "the hello");
        Socket(0).Close();
        await ServerPlaybackFixture.Until(() => _time.NextDue is { } due && due <= TimeSpan.FromMilliseconds(500), "the first wait");
        _time.Advance(TimeSpan.FromMilliseconds(500));
        await ServerPlaybackFixture.Until(() => _sockets.Count == 2 && Socket(1).Sent.Count == 1, "the second hello");

        _time.Advance(TimeSpan.FromSeconds(31));
        Socket(1).Close();

        await ServerPlaybackFixture.Until(() => _time.NextDue is { } due && due <= TimeSpan.FromMilliseconds(500), "a short wait again");
    }
}

/// <summary>The control socket's frames and address.</summary>
public sealed class PlaybackControlFramesTests
{
    [Fact]
    public void Command_IsReadWithItsPayloadAndIssuer()
    {
        var command = PlaybackControlFrames.ParseCommand(
            """{ "type": "command", "command_id": "c1", "session_id": "s1", "name": "seek", "payload": { "position_seconds": 61.5, "message": "Hi", "volume": "0.5" }, "issued_by": { "kind": "Admin " } }""",
            "s1");

        Assert.NotNull(command);
        Assert.Equal(("c1", "seek"), (command.CommandId, command.Name));
        Assert.True(command.IsFromAdministrator);
        Assert.Equal(61.5, command.Number("position", "position_seconds"));
        Assert.Equal("Hi", command.Text("message"));
        // A quoted number is no number.
        Assert.Null(command.Number("volume"));
        Assert.Null(command.Text("title"));
    }

    [Fact]
    public void BlankText_IsNone_AsTheWebReadsIt()
    {
        var command = PlaybackControlFrames.ParseCommand(
            """{ "type": "command", "command_id": "c1", "session_id": "s1", "name": "display_message", "payload": { "title": " ", "heading": "Hi", "message": "" } }""",
            "s1")!;

        Assert.Null(command.Text("title"));
        Assert.Equal("Hi", command.Text("title", "heading"));
        Assert.Null(command.Text("message"));
    }

    [Theory]
    [InlineData("""{ "type": "event", "session_id": "s1", "name": "markers_updated" }""")]
    [InlineData("""{ "type": "command", "command_id": "c1", "session_id": "s2", "name": "pause" }""")]
    [InlineData("""{ "type": "command", "session_id": "s1", "name": "pause" }""")]
    [InlineData("""{ "type": "command", "command_id": "c1", "session_id": "s1", "name": 4 }""")]
    [InlineData("""[1, 2]""")]
    [InlineData("""{ broken""")]
    [InlineData("")]
    public void AnythingButThisSessionsCommand_IsNone(string text) => Assert.Null(PlaybackControlFrames.ParseCommand(text, "s1"));

    [Fact]
    public void CommandWithoutPayload_HasNoValues()
    {
        var command = PlaybackControlFrames.ParseCommand("""{ "type": "command", "command_id": "c1", "session_id": "s1", "name": "pause" }""", "s1")!;

        Assert.Null(command.Text("message"));
        Assert.Null(command.Number("position"));
        Assert.False(command.IsFromAdministrator);
    }

    [Fact]
    public void Answers_AreTheProtocolsFrames()
    {
        Assert.Equal("""{"type":"ack","command_id":"c1","session_id":"s1","status":"accepted"}""", PlaybackControlFrames.Ack("s1", "c1"));
        Assert.Equal("""{"type":"result","command_id":"c1","session_id":"s1","status":"completed"}""", PlaybackControlFrames.Result("s1", "c1", true));
        Assert.Equal("""{"type":"result","command_id":"c1","session_id":"s1","status":"rejected","error":"unsupported"}""",
            PlaybackControlFrames.Result("s1", "c1", false, "unsupported"));
    }

    [Theory]
    [InlineData("https://nas.example/silo", "wss://nas.example/silo/api/v2/playback/sessions/s%201/control/ws")]
    [InlineData("http://192.168.1.6:8095/", "ws://192.168.1.6:8095/api/v2/playback/sessions/s%201/control/ws")]
    [InlineData("https://silo.example:443", "wss://silo.example/api/v2/playback/sessions/s%201/control/ws")]
    public void SocketAddress_IsTheServersOverWebSocket(string server, string expected) =>
        Assert.Equal(expected, PlaybackControlApi.SocketUri(new Uri(server), "s 1").AbsoluteUri);

    [Theory]
    [InlineData("AbC-123_xyz", 30, 14400, "silo.playback-control.v2", true)]
    [InlineData("AbC 123", 30, 14400, "silo.playback-control.v2", false)]
    [InlineData("AbC\r\nX: y", 30, 14400, "silo.playback-control.v2", false)]
    [InlineData("", 30, 14400, "silo.playback-control.v2", false)]
    [InlineData("AbC", 31, 14400, "silo.playback-control.v2", false)]
    [InlineData("AbC", 30, 14401, "silo.playback-control.v2", false)]
    [InlineData("AbC", 30, 14400, "silo.events.v2", false)]
    public void Ticket_IsOnlyTakenAsTheProtocolBoundsIt(string ticket, int expiresIn, int maxSeconds, string protocol, bool valid) =>
        Assert.Equal(valid, new PlaybackControlTicket(ticket, expiresIn, maxSeconds, protocol).IsValid);
}
