using System.Net;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Events;
using Granary.Core.Api.Notifications;
using Granary.Core.Playback;
using Granary.Core.Realtime;
using Granary.Core.ViewModels;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;
using Granary.Tests.Playback;
using Granary.Tests.Profiles;

namespace Granary.Tests.Realtime;

/// <summary>
/// An administrator signed in on profile 7 of a scripted server, the events stream kept over sockets the test talks
/// through, and the activity made of its frames. The clock stands still unless a test moves it.
/// </summary>
internal sealed class ServerActivityHarness : IAsyncDisposable
{
    public const string Hello = EventsLaneTests.Hello;

    private readonly List<FakeRealtimeSocket> _sockets = [];

    public ServerActivityHarness()
    {
        Library.Session.Account = Administrator;
        Library.Silo.On(HttpMethod.Post, EventsLaneTests.TicketPath, ScriptedSilo.Json(HttpStatusCode.OK, EventsLaneTests.Ticket));
        var badge = new NotificationBadge(Library.Session, new NotificationsApi(Library.Silo.Http), Library.Clock, Library.Logs.For<NotificationBadge>());
        Activity = new ServerActivity();
        Events = new RealtimeEvents(Library.Session, Lane, Library.ViewerState, new PlaybackStops(), badge, Activity, new ImmediateDispatcher(),
            Library.Clock, Library.Logs.For<RealtimeEvents>());
    }

    public static Account Administrator { get; } = new("1", "elena", "", "admin", [], true, false, null);

    public LibraryHarness Library { get; } = new();

    public ServerActivity Activity { get; }

    public RealtimeEvents Events { get; }

    public ServerActivityState State => Activity.State;

    public int SocketCount
    {
        get
        {
            lock (_sockets)
            {
                return _sockets.Count;
            }
        }
    }

    public FakeRealtimeSocket Socket(int index)
    {
        lock (_sockets)
        {
            return _sockets[index];
        }
    }

    /// <summary>A <c>subscribed</c> frame that took <paramref name="accepted"/> and turned down <paramref name="rejected"/>.</summary>
    public static string Subscribed(IEnumerable<string> accepted, IEnumerable<string>? rejected = null)
    {
        string channels = string.Join(", ", accepted.Select(channel => $"\"{channel}\""));
        string refused = string.Join(", ", (rejected ?? []).Select(channel => $$"""{ "channel": "{{channel}}", "code": "forbidden" }"""));
        return $$"""{ "type": "subscribed", "request_id": "req-1", "channels": [{{channels}}], "rejected": [{{refused}}] }""";
    }

    /// <summary>Starts the stream; its first connection subscribes and the server takes <paramref name="accepted"/>.</summary>
    public async Task<FakeRealtimeSocket> StartedAsync(params string[] accepted)
    {
        Events.Start();
        return await OpenedAsync(0, accepted);
    }

    /// <summary>Connection number <paramref name="index"/> says hello and takes <paramref name="accepted"/>, turning the other administrator's channels down.</summary>
    public async Task<FakeRealtimeSocket> OpenedAsync(int index, params string[] accepted)
    {
        await ServerPlaybackFixture.Until(() => SocketCount > index, "connection " + index);
        var socket = Socket(index);
        socket.Push(Hello);
        await ServerPlaybackFixture.Until(() => !socket.Sent.IsEmpty, "the subscribe");
        await PushedAsync(socket, Subscribed(accepted, EventsChannels.Admin.Except(accepted)));
        return socket;
    }

    /// <summary>Pushes <paramref name="frame"/> and waits until it was handled: frames are read off the test's thread.</summary>
    public async Task PushedAsync(FakeRealtimeSocket socket, string frame)
    {
        int handled = Events.Handled;
        socket.Push(frame);
        await ServerPlaybackFixture.Until(() => Events.Handled > handled, "the frame");
    }

    public async ValueTask DisposeAsync() => await Events.StopAsync();

    private EventsLane Lane() => new(new EventsApi(Library.Silo.Http), () =>
    {
        var socket = new FakeRealtimeSocket { Picks = EventsApi.Protocol };
        lock (_sockets)
        {
            _sockets.Add(socket);
        }
        return socket;
    }, Library.Clock, Library.Logs.For<EventsLane>());
}
