using System.Net;
using Granary.Core.Api.Events;
using Granary.Core.Api.Notifications;
using Granary.Core.Auth;
using Granary.Core.Realtime;
using Granary.Core.ViewModels;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.Realtime;

/// <summary>
/// The events stream kept for the signed-in profile, as the web and Android use it: a change of the profile's state on
/// another device, or of the catalog, reloads what shows it once the burst settled; progress at most every half minute;
/// a notification reads the sidebar's count again. The clock stands still unless a test moves it.
/// </summary>
public sealed class RealtimeEventsTests : IAsyncDisposable
{
    private readonly LibraryHarness _harness = new();
    private readonly List<FakeRealtimeSocket> _sockets = [];
    private readonly NotificationBadge _badge;
    private readonly RealtimeEvents _events;

    public RealtimeEventsTests()
    {
        _harness.Silo.On(HttpMethod.Post, EventsLaneTests.TicketPath, ScriptedSilo.Json(HttpStatusCode.OK, EventsLaneTests.Ticket));
        _harness.Answer("api/v2/notifications/capabilities", """{ "in_app": { "enabled": true } }""");
        _harness.Answer("api/v2/notifications/unread-count", """{ "count": 3 }""");
        var notifications = new NotificationsApi(_harness.Silo.Http);
        _badge = new NotificationBadge(_harness.Session, notifications, _harness.Clock, _harness.Logs.For<NotificationBadge>());
        _events = new RealtimeEvents(_harness.Session, Lane, _harness.ViewerState, _badge, new ImmediateDispatcher(), _harness.Clock,
            _harness.Logs.For<RealtimeEvents>());
    }

    private long Version => _harness.ViewerState.Version;

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

    private FakeRealtimeSocket Socket(int index)
    {
        lock (_sockets)
        {
            return _sockets[index];
        }
    }

    public async ValueTask DisposeAsync() => await _events.StopAsync();

    private EventsLane Lane() => new(new EventsApi(_harness.Silo.Http), () =>
    {
        var socket = new FakeRealtimeSocket { Picks = EventsApi.Protocol };
        lock (_sockets)
        {
            _sockets.Add(socket);
        }
        return socket;
    }, _harness.Clock, _harness.Logs.For<EventsLane>());

    private async Task<FakeRealtimeSocket> StartedAsync()
    {
        _events.Start();
        await ServerPlaybackFixture.Until(() => SocketCount == 1, "the connection");
        var socket = Socket(0);
        socket.Push(EventsLaneTests.Hello);
        socket.Push(EventsLaneTests.Subscribed);
        await ServerPlaybackFixture.Until(() => socket.Sent.Count == 1 && _events.Handled == 1, "the subscription");
        return socket;
    }

    private static string UserState(string profileId, string change) =>
        $$"""{ "type": "event", "channel": "user_state", "event": "user_state.changed", "event_id": "e-{{change}}", "timestamp": "t", "data": { "profile_id": "{{profileId}}", "content_id": "movie:heat", "change": "{{change}}" } }""";

    private const string CatalogChanged =
        """{ "type": "event", "channel": "catalog", "event": "catalog.item.changed", "event_id": "c1", "timestamp": "t", "data": { "content_id": "movie:heat" } }""";

    // Frames are read off the test's thread: the clock moves once they were handled.
    private async Task PushedAsync(FakeRealtimeSocket socket, string frame)
    {
        int handled = _events.Handled;
        socket.Push(frame);
        await ServerPlaybackFixture.Until(() => _events.Handled > handled, "the frame");
    }

    [Fact]
    public async Task Start_FollowsTheProfile_OnTheCatalogItsStateAndItsNotifications()
    {
        var socket = await StartedAsync();

        Assert.Contains("\"channels\":[\"catalog\",\"user_state\",\"notifications\"]", Assert.Single(socket.Sent), StringComparison.Ordinal);
        Assert.Single(_harness.Silo.SentTo(HttpMethod.Post, EventsLaneTests.TicketPath));
    }

    [Fact]
    public async Task TheProfilesChange_ReloadsOnceItSettled()
    {
        var socket = await StartedAsync();
        long before = Version;

        await PushedAsync(socket, UserState("7", "watched"));
        _harness.Clock.Advance(RealtimeEvents.SettleDelay - TimeSpan.FromMilliseconds(1));
        Assert.Equal(before, Version);

        _harness.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(before + 1, Version);
    }

    [Fact]
    public async Task AnotherProfilesChange_IsLeftAlone()
    {
        var socket = await StartedAsync();
        long before = Version;

        await PushedAsync(socket, UserState("8", "watched"));
        await PushedAsync(socket, UserState("8", "favorite"));
        _harness.Clock.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal(before, Version);
    }

    [Fact]
    public async Task Progress_ReloadsOncePerHalfMinute()
    {
        var socket = await StartedAsync();
        long before = Version;

        await PushedAsync(socket, UserState("7", "progress"));
        _harness.Clock.Advance(TimeSpan.FromSeconds(10));
        await PushedAsync(socket, UserState("7", "progress"));
        _harness.Clock.Advance(TimeSpan.FromSeconds(19));
        Assert.Equal(before, Version);

        _harness.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(before + 1, Version);
    }

    [Fact]
    public async Task ABurst_ReloadsOnce_AndALongOneAtLeastEveryHalfMinute()
    {
        var socket = await StartedAsync();
        long before = Version;

        for (int second = 0; second < 40; second++)
        {
            await PushedAsync(socket, CatalogChanged);
            _harness.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(before + 1, Version);

        _harness.Clock.Advance(RealtimeEvents.SettleDelay);
        Assert.Equal(before + 2, Version);
    }

    [Fact]
    public async Task ANotification_ReadsTheCountAgain()
    {
        var socket = await StartedAsync();
        Assert.Equal(0, _badge.UnreadCount);

        await PushedAsync(socket, """{ "type": "event", "channel": "notifications", "event": "notification.created", "event_id": "n1", "timestamp": "t", "data": { "id": "n1", "profile_id": "7" } }""");
        _harness.Clock.Advance(RealtimeEvents.BadgeDelay);

        await ServerPlaybackFixture.Until(() => _badge.UnreadCount == 3, "the count");
    }

    [Fact]
    public async Task AnAccessChange_ReloadsToo()
    {
        var socket = await StartedAsync();
        long before = Version;

        await PushedAsync(socket, """{ "type": "access_changed" }""");
        _harness.Clock.Advance(RealtimeEvents.SettleDelay);

        Assert.Equal(before + 1, Version);
    }

    [Fact]
    public async Task AConnectionBackAfterAWhile_CatchesUp_ABriefDropDoesNot()
    {
        await StartedAsync();
        long before = Version;

        _events.Handle(new EventsFrame(EventsFrameTypes.Subscribed) { Gap = TimeSpan.FromSeconds(2) }, "7");
        _harness.Clock.Advance(RealtimeEvents.SettleDelay);
        Assert.Equal(before, Version);

        _events.Handle(new EventsFrame(EventsFrameTypes.Subscribed) { Gap = TimeSpan.FromMinutes(1) }, "7");
        _harness.Clock.Advance(RealtimeEvents.SettleDelay);
        Assert.Equal(before + 1, Version);
    }

    [Fact]
    public async Task AnotherProfile_GetsAConnectionOfItsOwn_AndTheLastOnesReloadIsDropped()
    {
        var socket = await StartedAsync();
        long before = Version;
        await PushedAsync(socket, UserState("7", "watched"));

        _harness.Session.ProfileId = "8";
        _harness.Session.RaiseChanged();

        await ServerPlaybackFixture.Until(() => SocketCount == 2 && Socket(0).Disposed, "the new connection");
        _harness.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(before, Version);
    }

    [Fact]
    public async Task SigningOut_ClosesTheConnection()
    {
        var socket = await StartedAsync();

        _harness.Session.State = SessionState.SignedOut;
        _harness.Session.RaiseChanged();

        await ServerPlaybackFixture.Until(() => socket.Disposed, "the connection closed");
        Assert.Equal(1, SocketCount);
    }

    [Fact]
    public async Task WithoutAProfile_ThereIsNoConnection()
    {
        _harness.Session.ProfileId = null;

        _events.Start();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(0, SocketCount);
        Assert.Empty(_harness.Silo.SentTo(HttpMethod.Post, EventsLaneTests.TicketPath));
    }

    [Fact]
    public async Task TheSessionsAccountUpdate_KeepsTheConnection()
    {
        var socket = await StartedAsync();

        _harness.Session.RaiseChanged();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.False(socket.Disposed);
        Assert.Equal(1, SocketCount);
    }
}
