using System.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.Servers;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Realtime;

/// <summary>
/// The events stream kept for the signed-in profile, and what it means for the app, as the web and Silo's Android app use
/// it (docs/api-contract.md, 12). The profile's marks changed on another device (watched, a favourite, the watchlist,
/// history, a card taken off Home), the catalog changed, the access changed, or a connection came back after a while:
/// <see cref="ViewerStateChanges"/> once the burst settled (<see cref="SettleDelay"/>, but at least every
/// <see cref="LongestSettle"/> during a long one, a scan for instance), so Home reloads in place and other pages on their
/// next visit. A burst of only the catalog's or the access's news is reported as the catalog's alone
/// (<see cref="ViewerStateChanges.ReportCatalog"/>), so a title's page leaves it be. Another device's progress counts at
/// most once per <see cref="ProgressWindow"/>. A notification created or
/// read, or the unread ones sent on subscribing, read the sidebar's count again. Another profile gets a connection of its
/// own; signed out there is none. An administrator account's connection also asks for the administrator's channels
/// (<see cref="EventsChannels.Admin"/>), and all its frames go on to <see cref="ServerActivity"/>, which makes the
/// activity indicator of them; not while the profile has the indicator switched off (<see cref="ActivityIndicatorSetting"/>):
/// its connection then asks for what everyone's asks for, and the activity follows nothing.
/// </summary>
/// <remarks>
/// The stream does not say which device made a change, so this app's own come back too. A change of the profile's state
/// within <see cref="EchoWindow"/> of one this app reported, and progress while this app plays, are taken as its own echo:
/// the app already reloaded for them. A connection that subscribes after one that could not (the server was away, or Granary
/// started without it) says the server answers again (<see cref="ServerRecovery"/>); the server's own end of a connection
/// every five minutes, followed by the next at once, does not.
/// </remarks>
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

    /// <summary>How long after this app reported a change of its own the stream's news of the profile's state is its echo.</summary>
    public static readonly TimeSpan EchoWindow = TimeSpan.FromSeconds(5);

    private const string UserStateChanged = "user_state.changed";
    private const string ProgressChange = "progress";

    private static readonly string[] Channels = [EventsChannels.Catalog, EventsChannels.UserState, EventsChannels.Notifications];

    // An account that is an administrator asks for these too. Whether this profile acts as one is the server's to say:
    // it turns them down otherwise and keeps the connection.
    private static readonly string[] AdminChannels = [.. Channels, .. EventsChannels.Admin];

    // Set while this class reports, so its own reports are not taken for the app's.
    [ThreadStatic]
    private static bool t_reporting;

    private readonly Lock _gate = new();
    private readonly ISessionManager _session;
    private readonly Func<EventsLane> _lanes;
    private readonly ViewerStateChanges _changes;
    private readonly PlaybackStops _stops;
    private readonly NotificationBadge _badge;
    private readonly ServerActivity _activity;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<RealtimeEvents> _logger;
    private readonly ActivityIndicatorSetting? _indicator;
    private readonly ServerRecovery? _recovery;
    private readonly ITimer _settle;
    private readonly ITimer _progress;
    private readonly ITimer _count;
    private bool _started;
    private Following? _following;
    private EventsLane? _lane;
    private DateTimeOffset? _settlingSince;
    private bool _settlingForOthers;
    // The burst holds news of the profile's own state, or may (a connection back after a gap): its reload is the viewer's,
    // not only the catalog's.
    private bool _settlingForViewer;
    private bool _progressPending;
    private DateTimeOffset? _appChangedAt;
    // Since when the profile's connection is being replaced by one that asks for other channels; null once the new one
    // subscribed.
    private DateTimeOffset? _replacingSince;
    // The followed lane's connection under way has subscribed; a connection for this viewer (its lane, or the one it replaced
    // for other channels) ended without subscribing since the last that did: the server could not be reached.
    private bool _attemptSubscribed;
    private bool _unreachable;
    private int _handled;

    public RealtimeEvents(ISessionManager session, Func<EventsLane> lanes, ViewerStateChanges changes, PlaybackStops stops, NotificationBadge badge,
        ServerActivity activity, IUiDispatcher dispatcher, TimeProvider time, ILogger<RealtimeEvents> logger, ActivityIndicatorSetting? indicator = null,
        ServerRecovery? recovery = null)
    {
        _session = session;
        _lanes = lanes;
        _changes = changes;
        _stops = stops;
        _badge = badge;
        _activity = activity;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
        _indicator = indicator;
        _recovery = recovery;
        _settle = time.CreateTimer(_ => OnSettled(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _progress = time.CreateTimer(_ => OnProgressWindowClosed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _count = time.CreateTimer(_ => OnNotificationsSettled(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Frames handled so far (for tests, which move the clock once one arrived).</summary>
    internal int Handled => Volatile.Read(ref _handled);

    /// <summary>Follows the session: a connection for the signed-in profile from now on, also after a <see cref="StopAsync"/>.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }
            _started = true;
            _session.Changed += OnSessionChanged;
            _changes.Changed += OnAppChanged;
            if (_indicator is not null)
            {
                _indicator.PropertyChanged += OnIndicatorChanged;
            }
        }
        Follow();
    }

    /// <summary>Closes the connection and stops following the session. Never throws.</summary>
    public async Task StopAsync()
    {
        EventsLane? lane;
        lock (_gate)
        {
            if (_started)
            {
                _started = false;
                _session.Changed -= OnSessionChanged;
                _changes.Changed -= OnAppChanged;
                if (_indicator is not null)
                {
                    _indicator.PropertyChanged -= OnIndicatorChanged;
                }
            }
            lane = _lane;
            _lane = null;
            _following = null;
            ClearPendingLocked();
            _activity.Follow(null);
        }
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

    /// <summary>
    /// What <paramref name="frame"/>, sent for <paramref name="profileId"/>, means for the app. A frame of a lane
    /// (<paramref name="from"/>) counts only while that lane is the one followed: a replaced one may still hand on a last
    /// frame, and its "subscribed" would be taken for the new connection's start.
    /// </summary>
    internal void Handle(EventsFrame frame, string profileId, EventsLane? from = null)
    {
        try
        {
            bool recovered = false;
            lock (_gate)
            {
                if (_following?.ProfileId == profileId && (from is null || ReferenceEquals(_lane, from)))
                {
                    recovered = HandleLocked(frame, profileId);
                }
            }
            // Outside the lock: whoever waited for the server goes on in its own handler.
            if (recovered)
            {
                _recovery?.Report("the events connection");
            }
        }
        finally
        {
            Interlocked.Increment(ref _handled);
        }
    }

    // How the followed lane's connection stands: one that ended without subscribing means the server could not be reached.
    private void Connection(EventsConnection connection, EventsLane from)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_lane, from))
            {
                return;
            }
            if (connection == EventsConnection.Connecting)
            {
                _attemptSubscribed = false;
            }
            else if (connection == EventsConnection.Disconnected && !_attemptSubscribed)
            {
                _unreachable = true;
            }
        }
    }

    // True when the frame is a subscription that says the server answers again (SubscribedLocked).
    private bool HandleLocked(EventsFrame frame, string profileId)
    {
        switch (frame.Type)
        {
            case EventsFrameTypes.Subscribed:
                return SubscribedLocked(frame);
            case EventsFrameTypes.AccessChanged:
                SettleLocked(forOthers: true, forViewer: false);
                break;
            case EventsFrameTypes.Snapshot when frame.Channel == EventsChannels.Notifications:
                CountLocked();
                break;
            case EventsFrameTypes.Event when frame.Channel == EventsChannels.Catalog:
                SettleLocked(forOthers: true, forViewer: false);
                break;
            case EventsFrameTypes.Event when frame.Channel == EventsChannels.Notifications:
                CountLocked();
                break;
            case EventsFrameTypes.Event when frame.Channel == EventsChannels.UserState && frame.Event == UserStateChanged:
                HandleUserStateLocked(frame, profileId);
                break;
        }
        return false;
    }

    // A lane's first connection knows of no gap before it. One that replaced the same profile's connection has the time
    // since: what the server sent meanwhile was sent to nobody. True when a connection of the lane could not subscribe since
    // the last that did: the server answers again.
    private bool SubscribedLocked(EventsFrame frame)
    {
        var gap = frame.Gap ?? _time.GetUtcNow() - _replacingSince;
        _replacingSince = null;
        if (gap > CatchUpAfter)
        {
            SettleLocked(forOthers: true, forViewer: true);
        }
        _attemptSubscribed = true;
        bool recovered = _unreachable;
        _unreachable = false;
        return recovered;
    }

    private void HandleUserStateLocked(EventsFrame frame, string profileId)
    {
        // Bound to the profile, the stream sends only its own; one that names another stays out all the same.
        if (frame.Text("profile_id") is { Length: > 0 } owner && owner != profileId)
        {
            return;
        }
        if (_appChangedAt is { } changed && _time.GetUtcNow() - changed <= EchoWindow)
        {
            return;
        }
        if (frame.Text("change") != ProgressChange)
        {
            SettleLocked(forOthers: false, forViewer: true);
        }
        else if (!_stops.IsPlaying)
        {
            ProgressLocked();
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e) => Follow();

    // The indicator switched on or off in Settings: the connection asks for other channels.
    private void OnIndicatorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActivityIndicatorSetting.IsShown))
        {
            Follow();
        }
    }

    // The app reported a change of its own: what the stream says of the profile's state for a moment is its echo, and a
    // reload waiting only for such news has just happened.
    private void OnAppChanged(object? sender, EventArgs e)
    {
        if (t_reporting)
        {
            return;
        }
        lock (_gate)
        {
            _appChangedAt = _time.GetUtcNow();
            if (_settlingSince is not null && !_settlingForOthers)
            {
                _settlingSince = null;
                _settlingForViewer = false;
                _settle.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
            _progressPending = false;
            _progress.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    // Account updates raise the session's change too: only another server or profile (or none), or other channels to ask
    // for, changes the connection. The lane is replaced and started under the lock, so racing changes leave exactly one;
    // it starts off this thread.
    private void Follow()
    {
        EventsLane? previous;
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }
            var following = _session is { State: SessionState.Ready, ActiveServer: { } server, ProfileId: { Length: > 0 } profileId }
                ? new Following(server.Id, profileId, server.BaseUri,
                    _session.Account?.IsAdmin == true && _indicator?.IsShownFor(server.Id, profileId) != false)
                : null;
            if (following == _following)
            {
                return;
            }
            previous = _lane;
            _lane = null;
            _attemptSubscribed = false;
            // The same profile on the same server, only with other channels to ask for (the indicator switched, the
            // account made or unmade an administrator): what waits to be reported still counts, and so does a server the
            // last connection could not reach. Another viewer starts over: the session's change sends what waits.
            bool isSameViewer = following is not null && _following is not null
                && following with { FollowsActivity = _following.FollowsActivity } == _following;
            _following = following;
            if (isSameViewer)
            {
                _replacingSince ??= _time.GetUtcNow();
            }
            else
            {
                ClearPendingLocked();
                _unreachable = false;
            }
            // The activity starts over on the same change the lane is replaced on, and drops what the old one still sends.
            long feed = _activity.Follow(following is { FollowsActivity: true } ? following.BaseUri : null);
            if (following is not null)
            {
                var next = _lanes();
                _lane = next;
                _logger.LogInformation("Following the server's events for the profile");
                next.Follow(following.BaseUri, following.FollowsActivity ? AdminChannels : Channels, frame =>
                {
                    _activity.Handle(feed, frame);
                    Handle(frame, following.ProfileId, next);
                }, connection =>
                {
                    _activity.Connection(feed, connection);
                    Connection(connection, next);
                });
            }
        }
        if (previous is not null)
        {
            _ = previous.StopAsync();
        }
    }

    private void SettleLocked(bool forOthers, bool forViewer)
    {
        var now = _time.GetUtcNow();
        _settlingSince ??= now;
        _settlingForOthers |= forOthers;
        _settlingForViewer |= forViewer;
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
        _settlingForOthers = false;
        _settlingForViewer = false;
        _progressPending = false;
        _appChangedAt = null;
        _replacingSince = null;
        _settle.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _progress.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _count.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void OnSettled()
    {
        bool catalogOnly;
        lock (_gate)
        {
            if (_settlingSince is null)
            {
                return;
            }
            _settlingSince = null;
            _settlingForOthers = false;
            // This reload covers the progress that waited too.
            catalogOnly = !_settlingForViewer && !_progressPending;
            _settlingForViewer = false;
            _progressPending = false;
            _progress.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        Report(catalogOnly);
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
        Report(catalogOnly: false);
    }

    // Outside the lock: the pages' handlers run their own work.
    private void Report(bool catalogOnly)
    {
        t_reporting = true;
        try
        {
            if (catalogOnly)
            {
                _changes.ReportCatalog();
            }
            else
            {
                _changes.Report();
            }
        }
        finally
        {
            t_reporting = false;
        }
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

    // An account made or unmade an administrator, or a profile that switched its indicator, is another following: its
    // connection asks for other channels.
    private sealed record Following(string ServerId, string ProfileId, Uri BaseUri, bool FollowsActivity);
}
