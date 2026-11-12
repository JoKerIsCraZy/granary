using System.Collections.Frozen;

namespace Granary.Core.Realtime;

/// <summary>What the server does right now, as <see cref="ServerActivity"/> last knew it. Never changed once made.</summary>
public sealed record ServerActivityState
{
    /// <summary>Nothing to show: signed out, not an administrator, or the server gave no administrator's channel yet.</summary>
    public static ServerActivityState None { get; } = new();

    /// <summary>The administrator's channels the server gave the connection (<see cref="EventsChannels.Admin"/>).</summary>
    public FrozenSet<string> Channels { get; init; } = FrozenSet<string>.Empty;

    /// <summary>The server takes this profile as the acting administrator: there is an indicator.</summary>
    public bool IsAvailable => Channels.Count > 0;

    public EventsConnection Connection { get; init; } = EventsConnection.Disconnected;
}

/// <summary>
/// The server's activity for the acting administrator, as the web's indicator reads it
/// (<c>components/ServerActivity.tsx</c>), kept live by the events stream <see cref="RealtimeEvents"/> holds. The server
/// decides who sees it: the administrator's channels are asked for with every connection of an administrator account, and
/// only what a <c>subscribed</c> frame accepted is followed. A profile the server turns down has no indicator and causes
/// no requests.
/// </summary>
/// <remarks>
/// Frames and the connection's state arrive on the lane's thread. <see cref="Follow"/> hands out a number for each server
/// and profile followed; what arrives under an older number is dropped. <see cref="Changed"/> is raised on whichever
/// thread brought the news, outside the lock.
/// </remarks>
public sealed class ServerActivity
{
    private readonly Lock _gate = new();
    private Uri? _server;
    private long _feed;
    private ServerActivityState _state = ServerActivityState.None;

    public ServerActivityState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Raised after every change of <see cref="State"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Starts over for another server or profile, or for none (signed out, not an administrator account): what was known
    /// is gone. Answers the number its frames come under.
    /// </summary>
    internal long Follow(Uri? server)
    {
        long feed;
        bool changed;
        lock (_gate)
        {
            feed = ++_feed;
            _server = server;
            changed = !ReferenceEquals(_state, ServerActivityState.None);
            _state = ServerActivityState.None;
        }
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return feed;
    }

    /// <summary>How the connection of <paramref name="feed"/> stands.</summary>
    internal void Connection(long feed, EventsConnection connection)
    {
        lock (_gate)
        {
            if (feed != _feed || _server is null || _state.Connection == connection)
            {
                return;
            }
            _state = _state with { Connection = connection };
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A frame of the connection of <paramref name="feed"/>.</summary>
    internal void Handle(long feed, EventsFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            if (feed != _feed || _server is null || !HandleLocked(frame))
            {
                return;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // True when the state changed.
    private bool HandleLocked(EventsFrame frame)
    {
        switch (frame.Type)
        {
            case EventsFrameTypes.Subscribed:
                _state = _state with { Channels = frame.Accepted.Intersect(EventsChannels.Admin).ToFrozenSet(StringComparer.Ordinal) };
                return true;
            case EventsFrameTypes.AccessChanged:
                // Who this profile is to the server changed: nothing shows until the next connection says what it may see.
                _state = ServerActivityState.None with { Connection = _state.Connection };
                return true;
            default:
                return false;
        }
    }
}
