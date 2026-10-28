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

    private void Follow(EventsLane lane) => lane.Follow(ScriptedSilo.Base, Channels, frame =>
    {
        lock (_frames)
        {
            _frames.Add(frame);
        }
    });

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
        Assert.Equal(TimeSpan.Zero, Assert.Single(Frames()).Gap);
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
        Socket(3).Close();

        await WaitForTheWaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AnAccessChange_ComesBackAtOnce()
    {
        await using var lane = Lane();
        Follow(lane);
        await OpenAsync(0);

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

        Socket(0).Close(4001);

        await ServerPlaybackFixture.Until(() => SocketCount == 2, "the new connection, without a wait");
        Assert.Single(Frames(), f => f.Type == EventsFrameTypes.AccessChanged);
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
    public async Task Stop_ClosesTheConnection()
    {
        var lane = Lane();
        Follow(lane);
        await OpenAsync(0);

        await lane.StopAsync();

        Assert.True(lane.Running.IsCompleted);
        Assert.True(Socket(0).Disposed);
    }
}
