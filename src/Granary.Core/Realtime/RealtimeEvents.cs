using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Realtime;

/// <summary>
/// The events stream kept for the signed-in profile, and what it means for the app, as the web and Silo's Android app use
/// it (docs/api-contract.md, 12). The profile's marks changed on another device (watched, a favourite, the watchlist,
/// history, a card taken off Home), the catalog changed, the access changed, or a connection came back after a while:
/// <see cref="ViewerStateChanges"/> once the burst settled (<see cref="SettleDelay"/>, but at least every
/// <see cref="LongestSettle"/> during a long one, a scan for instance), so Home reloads in place and other pages on their
/// next visit. Another device's progress counts at most once per <see cref="ProgressWindow"/>. A notification created or
/// read, or the unread ones sent on subscribing, read the sidebar's count again. Another profile gets a connection of its
/// own; signed out there is none.
/// </summary>
public sealed class RealtimeEvents : IDisposable
{
    /// <summary>How long a burst of changes may go quiet before the app reloads.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2);

    /// <summary>The longest a reload waits while changes keep coming.</summary>
    public static readonly TimeSpan LongestSettle = TimeSpan.FromSeconds(30);

    /// <summary>Another device's playback reloads once per this window, not with every tick (the web's rule).</summary>
    public static readonly TimeSpan ProgressWindow = TimeSpan.FromSeconds(30);

    /// <summary>How long notifications may go quiet before the count is read again.</summary>
    public static readonly TimeSpan BadgeDelay = TimeSpan.FromSeconds(1);

    /// <summary>A connection back after a longer gap reloads once: what changed meanwhile was not sent.</summary>
    public static readonly TimeSpan CatchUpAfter = TimeSpan.FromSeconds(5);

    private const string UserStateChanged = "user_state.changed";
    private const string ProgressChange = "progress";

    private static readonly string[] Channels = [EventsChannels.Catalog, EventsChannels.UserState, EventsChannels.Notifications];

    private readonly Lock _gate = new();
    private readonly ISessionManager _session;
    private readonly Func<EventsLane> _lanes;
    private readonly ViewerStateChanges _changes;
    private readonly NotificationBadge _badge;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<RealtimeEvents> _logger;
    private readonly ITimer _settle;
    private readonly ITimer _progress;
    private readonly ITimer _count;
    private bool _started;
    private Following? _following;
    private EventsLane? _lane;
    private DateTimeOffset? _settlingSince;
    private bool _progressPending;
    private int _handled;

    public RealtimeEvents(ISessionManager session, Func<EventsLane> lanes, ViewerStateChanges changes, NotificationBadge badge, IUiDispatcher dispatcher,
        TimeProvider time, ILogger<RealtimeEvents> logger)
    {
        _session = session;
        _lanes = lanes;
        _changes = changes;
        _badge = badge;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
        _settle = time.CreateTimer(_ => OnSettled(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _progress = time.CreateTimer(_ => OnProgressWindowClosed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _count = time.CreateTimer(_ => OnNotificationsSettled(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Frames handled so far (for tests, which move the clock once one arrived).</summary>
    internal int Handled => Volatile.Read(ref _handled);

    /// <summary>Follows the session: a connection for the signed-in profile from now on.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }
            _started = true;
        }
        _session.Changed += OnSessionChanged;
        Follow();
    }

    /// <summary>Closes the connection and stops following the session. Never throws.</summary>
    public async Task StopAsync()
    {
        EventsLane? lane;
        lock (_gate)
        {
            _started = false;
            lane = _lane;
            _lane = null;
            _following = null;
            ClearPendingLocked();
        }
        _session.Changed -= OnSessionChanged;
        if (lane is not null)
        {
            await lane.StopAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _ = StopAsync();
        _settle.Dispose();
        _progress.Dispose();
        _count.Dispose();
    }

    /// <summary>What <paramref name="frame"/>, sent for <paramref name="profileId"/>, means for the app.</summary>
    internal void Handle(EventsFrame frame, string profileId)
    {
        try
        {
            lock (_gate)
            {
                if (_following?.ProfileId == profileId)
                {
                    HandleLocked(frame, profileId);
                }
            }
        }
        finally
        {
            Interlocked.Increment(ref _handled);
        }
    }

    private void HandleLocked(EventsFrame frame, string profileId)
    {
        switch (frame.Type)
        {
            case EventsFrameTypes.Subscribed when frame.Gap > CatchUpAfter:
            case EventsFrameTypes.AccessChanged:
                SettleLocked();
                break;
            case EventsFrameTypes.Snapshot when frame.Channel == EventsChannels.Notifications:
                CountLocked();
                break;
            case EventsFrameTypes.Event when frame.Channel == EventsChannels.Catalog:
                SettleLocked();
                break;
            case EventsFrameTypes.Event when frame.Channel == EventsChannels.Notifications:
                CountLocked();
                break;
            case EventsFrameTypes.Event when frame.Channel == EventsChannels.UserState && frame.Event == UserStateChanged:
                // Bound to the profile, the stream sends only its own; one that names another stays out all the same.
                if (frame.Text("profile_id") is { Length: > 0 } owner && owner != profileId)
                {
                    break;
                }
                if (frame.Text("change") == ProgressChange)
                {
                    ProgressLocked();
                }
                else
                {
                    SettleLocked();
                }
                break;
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e) => Follow();

    // Account updates raise the session's change too: only another server or profile (or none) changes the connection.
    private void Follow()
    {
        EventsLane? previous;
        EventsLane? next = null;
        Following? following;
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }
            following = _session is { State: SessionState.Ready, ActiveServer: { } server, ProfileId: { Length: > 0 } profileId }
                ? new Following(server.Id, profileId, server.BaseUri)
                : null;
            if (following == _following)
            {
                return;
            }
            previous = _lane;
            _following = following;
            ClearPendingLocked();
            if (following is not null)
            {
                next = _lanes();
            }
            _lane = next;
        }
        if (previous is not null)
        {
            _ = previous.StopAsync();
        }
        if (next is not null && following is not null)
        {
            _logger.LogInformation("Following the server's events for the profile");
            next.Follow(following.BaseUri, Channels, frame =>
            {
                // A lane that was replaced may still hand on a last frame.
                if (ReferenceEquals(Volatile.Read(ref _lane), next))
                {
                    Handle(frame, following.ProfileId);
                }
            });
        }
    }

    private void SettleLocked()
    {
        var now = _time.GetUtcNow();
        _settlingSince ??= now;
        var latest = _settlingSince.Value + LongestSettle - now;
        var due = latest < SettleDelay ? latest : SettleDelay;
        _settle.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private void ProgressLocked()
    {
        if (!_progressPending)
        {
            _progressPending = true;
            _progress.Change(ProgressWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private void CountLocked() => _count.Change(BadgeDelay, Timeout.InfiniteTimeSpan);

    private void ClearPendingLocked()
    {
        _settlingSince = null;
        _progressPending = false;
        _settle.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _progress.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _count.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // Reported outside the lock: the pages' handlers run their own work.
    private void OnSettled()
    {
        lock (_gate)
        {
            if (_settlingSince is null)
            {
                return;
            }
            _settlingSince = null;
            // This reload covers the progress that waited too.
            _progressPending = false;
            _progress.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        _changes.Report();
    }

    private void OnProgressWindowClosed()
    {
        lock (_gate)
        {
            if (!_progressPending)
            {
                return;
            }
            _progressPending = false;
        }
        _changes.Report();
    }

    // The count is the sidebar's, bound to the UI thread.
    private void OnNotificationsSettled()
    {
        lock (_gate)
        {
            if (_following is null)
            {
                return;
            }
        }
        _dispatcher.Post(() => _ = ReadCountAsync());
    }

    private async Task ReadCountAsync()
    {
        try
        {
            await _badge.RefreshAsync(force: true);
        }
        catch (Exception ex)
        {
            // Posted work: nothing may escape. A failed read keeps the last count.
            _logger.LogWarning(ex, "Could not read the unread notification count after an event");
        }
    }

    private sealed record Following(string ServerId, string ProfileId, Uri BaseUri);
}
