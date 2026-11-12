using System.Net;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Events;
using Granary.Core.Realtime;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.Realtime;

/// <summary>
/// The events stream's connection, as the web and Silo's Android app keep it: a fresh ticket per connection, the protocol
/// and the ticket offered, a subscribe after hello, and back after a loss with waits that grow until one connection held.
/// </summary>
public sealed class EventsLaneTests
{
    internal const string TicketPath = "api/v2/events/ws-ticket";
    internal const string TicketValue = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcde";
    internal const string Ticket = $$"""{ "ticket": "{{TicketValue}}", "expires_in": 30, "max_connection_seconds": 300, "protocol": "silo.events.v2" }""";
    internal const string Hello = """{ "type": "hello", "schema_version": 2, "connection_id": "c1", "available_channels": ["catalog", "user_state", "notifications"], "required_action": "subscribe" }""";
    internal const string Subscribed = """{ "type": "subscribed", "request_id": "req-1", "channels": ["catalog", "user_state", "notifications"] }""";

    private static readonly string[] Channels = ["catalog", "user_state", "notifications"];

    private readonly ScriptedSilo _silo = new();
    private readonly ManualTimeProvider _time = new();
    private readonly LogSink _logs = new();
    private readonly List<FakeRealtimeSocket> _sockets = [];
    private readonly List<EventsFrame> _frames = [];

    public EventsLaneTests()
    {
        _silo.On(HttpMethod.Post, TicketPath, ScriptedSilo.Json(HttpStatusCode.OK, Ticket));
    }

    private EventsLane Lane() => new(new EventsApi(_silo.Http), () =>
    {
        var socket = new FakeRealtimeSocket { Picks = EventsApi.Protocol };
        lock (_sockets)
        {
            _sockets.Add(socket);
        }
        return socket;
    }, _time, _logs.For<EventsLane>());

    private FakeRealtimeSocket Socket(int index)
    {
        lock (_sockets)
        {
            return _sockets[index];
        }
    }

    private int SocketCount
    {
        get
        {
            lock (_sockets)
            {
                return _sockets.Count;
            }
        }
    }

    private List<EventsFrame> Frames()
    {
        lock (_frames)
        {
            return [.. _frames];
        }
    }

    private readonly List<EventsConnection> _connections = [];

    private void Follow(EventsLane lane, IReadOnlyList<string>? channels = null) => lane.Follow(ScriptedSilo.Base, channels ?? Channels, frame =>
    {
        lock (_frames)
        {
            _frames.Add(frame);
        }
    }, connection =>
    {
        lock (_connections)
        {
            _connections.Add(connection);
        }
    });

    private List<EventsConnection> Connections()
    {
        lock (_connections)
        {
            return [.. _connections];
        }
    }

    // Opens connection number index: hello, then the server takes the subscription.
    private async Task OpenAsync(int index)
    {
        await ServerPlaybackFixture.Until(() => SocketCount == index + 1, "connection " + index);
        Socket(index).Push(Hello);
        await ServerPlaybackFixture.Until(() => Socket(index).Sent.Count == 1, "the subscribe");
        Socket(index).Push(Subscribed);
        await ServerPlaybackFixture.Until(() => Frames().Count(f => f.Type == EventsFrameTypes.Subscribed) == index + 1, "the subscription");
    }

    private async Task WaitForTheWaitAsync(TimeSpan wait) =>
        await ServerPlaybackFixture.Until(() => _time.NextDue == wait, $"a wait of {wait}");

    [Fact]
    public async Task Opening_OffersTheTicket_AndSubscribesAfterHello()
    {
        await using var lane = Lane();

        Follow(lane);
        await OpenAsync(0);

        Assert.Single(_silo.SentTo(HttpMethod.Post, TicketPath));
        Assert.Equal(new Uri("wss://nas.example/silo/api/v2/events/ws"), Socket(0).Uri);
        Assert.Equal(["silo.events.v2", "silo.ticket." + TicketValue], Socket(0).Offered);
        var subscribe = JsonDocument.Parse(Assert.Single(Socket(0).Sent)).RootElement;
        Assert.Equal("subscribe", subscribe.GetProperty("type").GetString());
        Assert.Equal(Channels, subscribe.GetProperty("channels").EnumerateArray().Select(c => c.GetString()));
        // The lane's first connection missed nothing.
        Assert.Null(Assert.Single(Frames()).Gap);
    }

    private static string HelloOffering(string? available) =>
        $$"""{ "type": "hello", "schema_version": 1, "connection_id": "c1"{{(available is null ? "" : $", \"available_channels\": {available}")}}, "required_action": "subscribe" }""";

    private async Task<string[]> AskedForAsync(string hello, params string[] wanted)
    {
        await using var lane = Lane();
        Follow(lane, wanted);
        await ServerPlaybackFixture.Until(() => SocketCount == 1, "the connection");
        Socket(0).Push(hello);
        await ServerPlaybackFixture.Until(() => Socket(0).Sent.Count == 1, "the subscribe");
        var subscribe = JsonDocument.Parse(Assert.Single(Socket(0).Sent)).RootElement;
        return [.. subscribe.GetProperty("channels").EnumerateArray().Select(channel => channel.GetString()!)];
    }

    [Fact]
    public async Task OnlyTheChannelsTheHelloOffers_AreAskedFor()
    {
        // An administrator account on a profile the server does not take as the acting administrator.
        string[] asked = await AskedForAsync(HelloOffering("""["catalog", "history_import", "user_state", "user_settings", "notifications"]"""),
            "catalog", "user_state", "notifications", "sessions", "tasks", "scans", "download_preparations");

        Assert.Equal(["catalog", "user_state", "notifications"], asked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("\"all\"")]
    [InlineData("[]")]
    [InlineData("""["jobs", "settings"]""")]
    public async Task AHelloThatOffersNothingToGoBy_AsksForEverything_AndTheServersAnswerDecides(string? available)
    {
        string[] asked = await AskedForAsync(HelloOffering(available), "catalog", "user_state", "sessions");

        Assert.Equal(["catalog", "user_state", "sessions"], asked);
    }

    [Fact]
    public async Task TheChannelsTurnedDown_AreNamedInTheLog_OnlyWhereTheyLookLikeChannels()
    {
        await using var lane = Lane();
        Follow(lane);
        await ServerPlaybackFixture.Until(() => SocketCount == 1, "the connection");
        Socket(0).Push(Hello);

        Socket(0).Push("""{ "type": "subscribed", "request_id": "req-1", "channels": ["catalog"], "rejected": [ { "channel": "notifications", "code": "profile_required" }, { "channel": "x\r\nWARN forged line", "code": "unknown_channel" } ] }""");

        await ServerPlaybackFixture.Until(() => Frames().Count == 1, "the subscription");
        string line = Assert.Single(_logs.Lines, each => each.Contains("turned down", StringComparison.Ordinal));
        Assert.Contains("notifications, (unnamed)", line, StringComparison.Ordinal);
        Assert.DoesNotContain("forged", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatIsNoFrame_IsNotedWithoutItsText()
    {
        await using var lane = Lane();
        Follow(lane);
        await OpenAsync(0);

        // What is kept of a frame that was cut, when its head does not say what it is.
        Socket(0).Push("""{"data":[{"session_id":"s1","media_title":"Heat""");
        Socket(0).Push("""{ "type": "event", "channel": "catalog", "event": "catalog.item.changed", "event_id": "e1", "timestamp": "t", "data": {} }""");

        await ServerPlaybackFixture.Until(() => Frames().Count == 2, "the next frame");
        string line = Assert.Single(_logs.Lines, each => each.Contains("could not be read", StringComparison.Ordinal));
        Assert.DoesNotContain("Heat", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Frames_AreHandedOn()
    {
        await using var lane = Lane();
        Follow(lane);
        await OpenAsync(0);

        Socket(0).Push("""{ "type": "event", "channel": "catalog", "event": "catalog.item.changed", "event_id": "e1", "timestamp": "t", "data": {} }""");
        Socket(0).Push("not json");
        Socket(0).Push("""{ "type": "snapshot", "channel": "notifications", "timestamp": "t", "data": [] }""");

        await ServerPlaybackFixture.Until(() => Frames().Count == 3, "the frames");
        Assert.Equal([EventsFrameTypes.Subscribed, EventsFrameTypes.Event, EventsFrameTypes.Snapshot], Frames().Select(f => f.Type));
    }

    [Fact]
    public async Task AFrameCutShort_IsHandedOnAsNewsOfItsChannel()
    {
        await using var lane = Lane();
        Follow(lane);
        await OpenAsync(0);

        // What the socket keeps of a frame larger than the lane takes.
        Socket(0).Push("""{"type":"event","channel":"sessions","event":"sessions.replaced","event_id":"e1","timestamp":"t","data":[{"session_id":"s1","media_ti""");
        Socket(0).Push("""{ "type": "event", "channel": "catalog", "event": "catalog.item.changed", "event_id": "e2", "timestamp": "t", "data": {} }""");

        await ServerPlaybackFixture.Until(() => Frames().Count == 3, "the frames");
        Assert.Equal([(null, false), ("sessions", true), ("catalog", false)], Frames().Select(f => (f.Channel, f.IsCut)));
    }

    [Fact]
    public async Task TheConnectionsState_IsToldAsItChanges()
    {
        await using var lane = Lane();
        Follow(lane);
        await OpenAsync(0);
        Assert.Equal([EventsConnection.Connecting, EventsConnection.Live], Connections());

        Socket(0).Close();
        await WaitForTheWaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(EventsConnection.Disconnected, Connections()[^1]);

        _time.Advance(TimeSpan.FromSeconds(1));
        await OpenAsync(1);
        Assert.Equal([EventsConnection.Connecting, EventsConnection.Live, EventsConnection.Disconnected, EventsConnection.Connecting, EventsConnection.Live],
            Connections());
    }

    [Fact]
    public async Task ALostConnection_ComesBackWithAFreshTicket_AndSaysHowLongItWasGone()
    {
        await using var lane = Lane();
        Follow(lane);
        await OpenAsync(0);

        Socket(0).Close();
        await WaitForTheWaitAsync(TimeSpan.FromSeconds(1));
        _time.Advance(TimeSpan.FromSeconds(1));
        await OpenAsync(1);

        Assert.Equal(2, _silo.SentTo(HttpMethod.Post, TicketPath).Count);
        Assert.True(Socket(0).Disposed);
        Assert.Equal(TimeSpan.FromSeconds(1), Frames()[^1].Gap);
    }

    [Fact]
    public async Task ConnectionsThatNeverSubscribe_ComeBackEverMoreSlowly_UntilOneDoes()
    {
        await using var lane = Lane();
        Follow(lane);
        await ServerPlaybackFixture.Until(() => SocketCount == 1, "the first connection");

        foreach (var (wait, index) in new[] { (1, 1), (2, 2), (4, 3) })
        {
            Socket(index - 1).Close();
            await WaitForTheWaitAsync(TimeSpan.FromSeconds(wait));
            _time.Advance(TimeSpan.FromSeconds(wait));
            await ServerPlaybackFixture.Until(() => SocketCount == index + 1, "connection " + index);
        }
        Socket(3).Push(Hello);
        Socket(3).Push(Subscribed);
        await ServerPlaybackFixture.Until(() => Frames().Count == 1, "the subscription");
        _time.Advance(TimeSpan.FromSeconds(2));
        Socket(3).Close();

        await WaitForTheWaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AConnectionClosedAtOnce_DoesNotStartTheWaitsOver()
    {
        await using var lane = Lane();
        Follow(lane);
        await OpenAsync(0);
        Socket(0).Close();
        await WaitForTheWaitAsync(TimeSpan.FromSeconds(1));
        _time.Advance(TimeSpan.FromSeconds(1));

        await OpenAsync(1);
        Socket(1).Close();

        await WaitForTheWaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AnAccessChange_ComesBackAtOnce()
    {
        await using var lane = Lane();
        Follow(lane);
        await OpenAsync(0);
        _time.Advance(TimeSpan.FromSeconds(2));

        Socket(0).Push("""{ "type": "access_changed" }""");
        Socket(0).Close(4001);

        await ServerPlaybackFixture.Until(() => SocketCount == 2, "the new connection, without a wait");
        Assert.Single(Frames(), f => f.Type == EventsFrameTypes.AccessChanged);
    }

    [Fact]
    public async Task TheAccessChangedCode_AloneCountsAsTheChange()
    {
        await using var lane = Lane();
        Follow(lane);
        await OpenAsync(0);
        _time.Advance(TimeSpan.FromSeconds(2));

        Socket(0).Close(4001);

        await ServerPlaybackFixture.Until(() => SocketCount == 2, "the new connection, without a wait");
        Assert.Single(Frames(), f => f.Type == EventsFrameTypes.AccessChanged);
    }

    [Fact]
    public async Task AServerThatChangesTheAccessOfEveryConnection_IsNotAskedInALoop()
    {
        await using var lane = Lane();
        Follow(lane);
        await OpenAsync(0);

        Socket(0).Close(4001);
        await WaitForTheWaitAsync(TimeSpan.FromSeconds(1));
        _time.Advance(TimeSpan.FromSeconds(1));
        await OpenAsync(1);
        Socket(1).Close(4001);

        await WaitForTheWaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, SocketCount);
    }

    [Fact]
    public async Task AConnection_EndsWhenItsTimeIsUp()
    {
        await using var lane = Lane();
        Follow(lane);
        await OpenAsync(0);

        await WaitForTheWaitAsync(TimeSpan.FromSeconds(300));
        _time.Advance(TimeSpan.FromSeconds(300));
        await WaitForTheWaitAsync(TimeSpan.FromSeconds(1));
        _time.Advance(TimeSpan.FromSeconds(1));

        await ServerPlaybackFixture.Until(() => SocketCount == 2, "the next connection");
    }

    [Theory]
    [InlineData(404)]
    [InlineData(501)]
    public async Task AServerWithoutEvents_HasNoLane(int status)
    {
        _silo.On(HttpMethod.Post, TicketPath, ScriptedSilo.Problem(status, "not_found"));
        await using var lane = Lane();

        Follow(lane);

        await lane.Running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, SocketCount);
        Assert.Equal([EventsConnection.Connecting, EventsConnection.Disconnected], Connections());
    }

    [Fact]
    public async Task ABusyServer_IsAskedAgain()
    {
        _silo.On(HttpMethod.Post, TicketPath, ScriptedSilo.Problem(503, ProblemCodes.DependencyUnavailable), ScriptedSilo.Json(HttpStatusCode.OK, Ticket));
        await using var lane = Lane();

        Follow(lane);
        await WaitForTheWaitAsync(TimeSpan.FromSeconds(1));
        _time.Advance(TimeSpan.FromSeconds(1));

        await OpenAsync(0);
    }

    [Fact]
    public async Task AnotherProtocol_IsNoConnection()
    {
        await using var lane = new EventsLane(new EventsApi(_silo.Http), () =>
        {
            var socket = new FakeRealtimeSocket { Picks = "silo.events.v1" };
            lock (_sockets)
            {
                _sockets.Add(socket);
            }
            return socket;
        }, _time, _logs.For<EventsLane>());

        Follow(lane);

        await WaitForTheWaitAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(Socket(0).Sent);
        Assert.DoesNotContain(_logs.Lines, line => line.Contains(TicketValue, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_ClosesTheConnection_ForGood()
    {
        var lane = Lane();
        Follow(lane);
        await OpenAsync(0);

        await lane.StopAsync();
        Follow(lane);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Its end is the caller's own doing and is not reported.
        Assert.Equal([EventsConnection.Connecting, EventsConnection.Live], Connections());
        Assert.True(lane.Running.IsCompleted);
        Assert.True(Socket(0).Disposed);
        Assert.Equal(1, SocketCount);
        Assert.Single(_silo.SentTo(HttpMethod.Post, TicketPath));
    }

    [Fact]
    public async Task FollowingAgain_LeavesOneConnection()
    {
        await using var lane = Lane();
        Follow(lane);
        await OpenAsync(0);

        Follow(lane);

        await ServerPlaybackFixture.Until(() => SocketCount == 2 && Socket(0).Disposed, "the second connection in place of the first");
        Assert.False(Socket(1).Disposed);
    }

    [Fact]
    public async Task FollowingFromManyThreadsAtOnce_LeavesOneConnection()
    {
        await using var lane = Lane();

        Parallel.For(0, 16, _ => Follow(lane));

        await ServerPlaybackFixture.Until(() => SocketCount >= 1 && Live() == 1, "a single live connection");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, Live());

        int Live()
        {
            lock (_sockets)
            {
                return _sockets.Count(socket => !socket.Disposed);
            }
        }
    }
}
