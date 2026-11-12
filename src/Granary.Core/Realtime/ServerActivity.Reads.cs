using Granary.Core.Api;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Realtime;

// The lists read from the server when the stream says they changed, paced as the web's scheduler paces its own
// (components/realtimeQueryRefresh.ts): the first news reads at once, and more news while a read runs or within
// RefreshWindow of it makes one more read when the window closes. task.updated alone comes four times a second per task.
// A read that fails keeps what was shown and makes its window longer, twice as long with each failure in a row, so a
// server that answers 429 or 500 is not asked every five seconds while the stream keeps bringing news.
public sealed partial class ServerActivity
{
    /// <summary>How long after a read the next one of the same list waits.</summary>
    public static readonly TimeSpan RefreshWindow = TimeSpan.FromSeconds(5);

    /// <summary>The longest a list is left alone after reads that failed, unless the server asks for longer.</summary>
    public static readonly TimeSpan LongestRetryWindow = TimeSpan.FromSeconds(60);

    /// <summary>The longest wait a server's <c>Retry-After</c> is given.</summary>
    public static readonly TimeSpan LongestRetryAfter = TimeSpan.FromMinutes(5);

    // The failures in a row after which the window no longer grows: 5 s, 10, 20, 40, then LongestRetryWindow.
    private const int FailuresToLongestWindow = 5;

    /// <summary>Reads a list and answers how the state shows it.</summary>
    private delegate Task<Func<ServerActivityState, ServerActivityState>> Read(Uri server, CancellationToken cancellationToken);

    private Reread Source(string channel, string what, Read read, Func<ServerActivityState, ServerActivityState> clear, Action? failed = null)
    {
        var reread = new Reread(channel, what, read, clear) { Failed = failed };
        reread.Window = _time.CreateTimer(_ => OnWindowClosed(reread), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return reread;
    }

    // News of a list: read now, or once more when the read or the window that runs is over. Nothing is read whose channel
    // the server did not give this connection, whoever asks (a frame, or a timer that fired late).
    private void CueLocked(Reread reread)
    {
        if (reread.IsOff || _following is not { } following || !_state.Channels.Contains(reread.Channel))
        {
            return;
        }
        if (reread.IsReading || reread.IsWaiting)
        {
            reread.IsDue = true;
            return;
        }
        reread.IsReading = true;
        reread.IsDue = false;
        // Off this thread: the lock is held, and a frame's thread is the lane's.
        _ = Task.Run(() => ReadAsync(reread, following), CancellationToken.None);
    }

    private async Task ReadAsync(Reread reread, Following following)
    {
        Func<ServerActivityState, ServerActivityState>? show = null;
        Exception? failure = null;
        bool offered = true;
        try
        {
            show = await reread.Read(following.Server, following.Token).ConfigureAwait(false);
        }
        catch (SiloApiException ex) when (ex.Status is 403 or 404 or 501 or 503)
        {
            offered = false;
            _logger.LogInformation("The server has no {What} for the activity indicator ({Status})", reread.What, ex.Status);
        }
        catch (Exception ex)
        {
            // Nothing awaits this read, so nothing may escape it. What was shown stays; the next news reads again.
            failure = ex;
        }
        int failures = 0;
        bool recovered = false;
        lock (_gate)
        {
            // Another server or profile, or an access change, since the read began: its answer is nobody's.
            if (!ReferenceEquals(_following, following))
            {
                return;
            }
            reread.IsReading = false;
            // A later connection of the same profile no longer has the channel: the answer shows nothing.
            if (!_state.Channels.Contains(reread.Channel))
            {
                return;
            }
            if (!offered)
            {
                reread.IsOff = true;
                reread.IsDue = false;
                reread.Failures = 0;
                _state = reread.Clear(_state);
            }
            else if (show is null)
            {
                failures = ++reread.Failures;
                reread.IsWaiting = true;
                reread.Window.Change(RetryWindow(failures, failure), Timeout.InfiniteTimeSpan);
                reread.Failed?.Invoke();
            }
            else
            {
                recovered = reread.Failures > 0;
                reread.Failures = 0;
                reread.IsWaiting = true;
                reread.Window.Change(RefreshWindow, Timeout.InfiniteTimeSpan);
                _state = show(_state);
            }
        }
        if (failures > 0)
        {
            LogFailure(reread, failures, failure);
            return;
        }
        if (recovered)
        {
            _logger.LogInformation("The {What} for the activity indicator can be read again", reread.What);
        }
        Notify();
    }

    // After the first failure the window is as after any read, then twice as long each time up to LongestRetryWindow;
    // what the server asks for when that is more.
    private static TimeSpan RetryWindow(int failures, Exception? failure)
    {
        var window = failures >= FailuresToLongestWindow ? LongestRetryWindow : RefreshWindow * Math.Pow(2, failures - 1);
        if (failure is SiloApiException { RetryAfter: { } asked } && asked > window)
        {
            return asked < LongestRetryAfter ? asked : LongestRetryAfter;
        }
        return window;
    }

    // The first failure of an outage is a warning with its cause; the ones after it would only repeat it.
    private void LogFailure(Reread reread, int failures, Exception? failure)
    {
        if (failures == 1)
        {
            _logger.LogWarning(failure, "Could not read the {What} for the activity indicator; it is read again with the next news", reread.What);
        }
        else
        {
            _logger.LogDebug("Still could not read the {What} for the activity indicator ({Failure}, {Failures} in a row)", reread.What,
                failure?.GetType().Name, failures);
        }
    }

    private void OnWindowClosed(Reread reread)
    {
        lock (_gate)
        {
            reread.IsWaiting = false;
            if (reread.IsDue)
            {
                CueLocked(reread);
            }
        }
    }

    /// <summary>One list and where its reading stands; every member is the lock's.</summary>
    private sealed class Reread(string channel, string what, Read read, Func<ServerActivityState, ServerActivityState> clear)
    {
        /// <summary>The channel whose news asks for the read; without it the list is not offered.</summary>
        public string Channel { get; } = channel;

        /// <summary>The list's name in a log line.</summary>
        public string What { get; } = what;

        public Read Read { get; } = read;

        /// <summary>The state without this list.</summary>
        public Func<ServerActivityState, ServerActivityState> Clear { get; } = clear;

        /// <summary>Called under the lock when a read failed.</summary>
        public Action? Failed { get; init; }

        /// <summary>Runs from a read's answer on, for <see cref="RefreshWindow"/>, longer after a failure.</summary>
        public ITimer Window { get; set; } = null!;

        public bool IsReading { get; set; }

        /// <summary>Within the window after a read.</summary>
        public bool IsWaiting { get; set; }

        /// <summary>News came while reading or waiting.</summary>
        public bool IsDue { get; set; }

        /// <summary>The server does not offer the list: not asked for until the next connection.</summary>
        public bool IsOff { get; set; }

        /// <summary>Reads that failed in a row; the window after a read grows with them.</summary>
        public int Failures { get; set; }
    }
}
