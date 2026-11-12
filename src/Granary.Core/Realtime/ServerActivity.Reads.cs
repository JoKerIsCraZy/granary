using Granary.Core.Api;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Realtime;

// The lists read from the server when the stream says they changed, paced as the web's scheduler paces its own
// (components/realtimeQueryRefresh.ts): the first news reads at once, and more news while a read runs or within
// RefreshWindow of it makes one more read when the window closes. task.updated alone comes four times a second per task.
public sealed partial class ServerActivity
{
    /// <summary>How long after a read the next one of the same list waits.</summary>
    public static readonly TimeSpan RefreshWindow = TimeSpan.FromSeconds(5);

    /// <summary>Reads a list and answers how the state shows it.</summary>
    private delegate Task<Func<ServerActivityState, ServerActivityState>> Read(Uri server, CancellationToken cancellationToken);

    private Reread Source(string channel, string what, Read read, Func<ServerActivityState, ServerActivityState> clear)
    {
        var reread = new Reread(channel, what, read, clear);
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
            if (!following.Token.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not read the {What} for the activity indicator", reread.What);
            }
        }
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
                _state = reread.Clear(_state);
            }
            else
            {
                reread.IsWaiting = true;
                reread.Window.Change(RefreshWindow, Timeout.InfiniteTimeSpan);
                if (show is null)
                {
                    return;
                }
                _state = show(_state);
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
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

        /// <summary>Runs from a read's answer on, for <see cref="RefreshWindow"/>.</summary>
        public ITimer Window { get; set; } = null!;

        public bool IsReading { get; set; }

        /// <summary>Within the window after a read.</summary>
        public bool IsWaiting { get; set; }

        /// <summary>News came while reading or waiting.</summary>
        public bool IsDue { get; set; }

        /// <summary>The server does not offer the list: not asked for until the next connection.</summary>
        public bool IsOff { get; set; }
    }
}
