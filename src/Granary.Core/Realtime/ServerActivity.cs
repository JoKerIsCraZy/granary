using System.Collections.Frozen;
using Granary.Core.Api.Admin;
using Microsoft.Extensions.Logging;

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

    /// <summary>The playback sessions running now; null while there is none to show (not read yet, or not offered).</summary>
    public IReadOnlyList<AdminPlaybackSession>? Sessions { get; init; }

    /// <summary>Every scheduled task, the idle ones too; null as <see cref="Sessions"/>.</summary>
    public IReadOnlyList<AdminTask>? Tasks { get; init; }

    /// <summary>
    /// The library scans the stream told of, the queued and running ones among them; null until a connection that was
    /// given their channel said anything of them. They are known from the stream alone.
    /// </summary>
    public IReadOnlyList<AdminScanRun>? Scans { get; init; }

    /// <summary>The libraries' names by id, for the scans' rows; null while they are not read.</summary>
    public FrozenDictionary<string, string>? LibraryNames { get; init; }
}

/// <summary>
/// The server's activity for the acting administrator, as the web's indicator reads it
/// (<c>components/ServerActivity.tsx</c>), kept live by the events stream <see cref="RealtimeEvents"/> holds. The server
/// decides who sees it: the administrator's channels are asked for with every connection of an administrator account, and
/// only what a <c>subscribed</c> frame accepted is followed. A profile the server turns down has no indicator and causes
/// no requests.
/// </summary>
/// <remarks>
/// <para>
/// Each source stands alone. One the server does not offer (its channel turned down, or its list answering 403, 404, 501
/// or 503) is not shown and not asked for again until the next connection; any other failed read keeps what was shown.
/// </para>
/// <para>
/// Frames and the connection's state arrive on the lane's thread, answers on the thread pool, timers on theirs.
/// <see cref="Follow"/> hands out a number for each server and profile followed; frames under an older number are
/// dropped, and so are the answers of reads started for an earlier following or before an access change.
/// <see cref="Changed"/> is raised on whichever thread brought the news, outside the lock.
/// </para>
/// </remarks>
public sealed partial class ServerActivity : IDisposable
{
    private const string SessionsReplaced = "sessions.replaced";
    private const string TaskUpdated = "task.updated";

    private readonly AdminApi _admin;
    private readonly TimeProvider _time;
    private readonly ILogger<ServerActivity> _logger;
    private readonly Lock _gate = new();
    private readonly Reread _sessions;
    private readonly Reread _tasks;
    private readonly Reread _libraries;
    private readonly Reread[] _rereads;
    private Following? _following;
    private long _feed;
    private ServerActivityState _state = ServerActivityState.None;

    public ServerActivity(AdminApi admin, TimeProvider time, ILogger<ServerActivity> logger)
    {
        _admin = admin;
        _time = time;
        _logger = logger;
        _sessions = Source(EventsChannels.Sessions, "playback sessions",
            async (server, cancellationToken) =>
            {
                var sessions = (await _admin.ListSessionsAsync(server, cancellationToken).ConfigureAwait(false)).Items;
                return state => state with { Sessions = sessions };
            },
            state => state with { Sessions = null });
        _tasks = Source(EventsChannels.Tasks, "scheduled tasks",
            async (server, cancellationToken) =>
            {
                var tasks = (await _admin.ListTasksAsync(server, cancellationToken).ConfigureAwait(false)).Items;
                return state => state with { Tasks = tasks };
            },
            state => state with { Tasks = null });
        _libraries = LibrariesSource();
        _rereads = [_sessions, _tasks, _libraries];
    }

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

    public void Dispose()
    {
        Follow(null);
        foreach (var reread in _rereads)
        {
            reread.Window.Dispose();
        }
    }

    /// <summary>
    /// Starts over for another server or profile, or for none (signed out, not an administrator account): what was known
    /// is gone. Answers the number its frames come under.
    /// </summary>
    internal long Follow(Uri? server)
    {
        long feed;
        bool changed;
        Following? previous;
        lock (_gate)
        {
            feed = ++_feed;
            previous = StartOverLocked(server);
            changed = !ReferenceEquals(_state, ServerActivityState.None);
            _state = ServerActivityState.None;
        }
        previous?.Cancel();
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
            if (feed != _feed || _following is null || _state.Connection == connection)
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
        Following? previous = null;
        lock (_gate)
        {
            if (feed != _feed || _following is not { } following)
            {
                return;
            }
            switch (frame.Type)
            {
                case EventsFrameTypes.Subscribed:
                    SubscribedLocked(frame);
                    break;
                case EventsFrameTypes.AccessChanged:
                    // Who this profile is to the server changed: nothing shows, and no answer on its way counts, until
                    // the next connection says what it may see.
                    previous = StartOverLocked(following.Server);
                    _state = ServerActivityState.None with { Connection = _state.Connection };
                    break;
                case EventsFrameTypes.Event when _state.Channels.Contains(frame.Channel ?? ""):
                    if (!EventLocked(frame))
                    {
                        return;
                    }
                    break;
                case EventsFrameTypes.Snapshot when frame.Channel == EventsChannels.Scans && _state.Channels.Contains(EventsChannels.Scans):
                    if (!ScansSnapshotLocked(frame))
                    {
                        return;
                    }
                    break;
                default:
                    return;
            }
        }
        previous?.Cancel();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // What the server gave this connection is all there is to follow: each source it gave is read, the others are gone.
    private void SubscribedLocked(EventsFrame frame)
    {
        var channels = frame.Accepted.Intersect(EventsChannels.Admin).ToFrozenSet(StringComparer.Ordinal);
        // The scans known so far stay until this connection's snapshot replaces them; without the channel they are gone.
        _state = _state with { Channels = channels, Scans = channels.Contains(EventsChannels.Scans) ? _state.Scans : null };
        foreach (var reread in _rereads)
        {
            reread.IsOff = !channels.Contains(reread.Channel);
            if (reread.IsOff)
            {
                _state = reread.Clear(_state);
            }
            else
            {
                CueLocked(reread);
            }
        }
    }

    // True when the state changed with the event itself; a read it asks for reports its own change.
    private bool EventLocked(EventsFrame frame)
    {
        switch (frame.Channel)
        {
            // As the web: the frame only says that the sessions changed, and may be cut for its size.
            case EventsChannels.Sessions when frame.Event == SessionsReplaced:
                CueLocked(_sessions);
                return false;
            // The frame's task is one server's view and never a hidden worker's whole story: the list is read again, as
            // on the web. A running task sends this four times a second.
            case EventsChannels.Tasks when frame.Event == TaskUpdated:
                CueLocked(_tasks);
                return false;
            case EventsChannels.Scans:
                return ScanEventLocked(frame);
            default:
                return false;
        }
    }

    // The reads of what was followed so far no longer count; the caller cancels them once the lock is released.
    private Following? StartOverLocked(Uri? server)
    {
        var previous = _following;
        _following = server is null ? null : new Following(server);
        foreach (var reread in _rereads)
        {
            reread.IsReading = false;
            reread.IsWaiting = false;
            reread.IsDue = false;
            reread.IsOff = false;
            reread.Window.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        _askedLibraries.Clear();
        return previous;
    }

    /// <summary>One server and profile followed, or the same after an access change: what its reads are cancelled by.</summary>
    private sealed class Following(Uri server)
    {
        // Never disposed: a read may still hold its token, and it has no timer or link to release.
        private readonly CancellationTokenSource _reads = new();

        public Uri Server { get; } = server;

        public CancellationToken Token => _reads.Token;

        public void Cancel() => _reads.Cancel();
    }
}
