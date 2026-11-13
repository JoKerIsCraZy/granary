using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Granary.Core.Api.Admin;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Realtime;

// Library scans exist only on the stream: the server has no list of the active ones to read. As the web keeps them
// (RealtimeEventsProvider.tsx, hydrateScans and applyScanUpdate): the channel's snapshot replaces what is known, and each
// scan.* event brings one run, which takes its place, joins the list, or leaves it once it is no longer queued or running.
//
// Where the web trusts what it has, Granary says when it cannot know. The scans kept from an earlier connection are only
// a guess until the new connection's snapshot confirms or replaces them (an end that came while the stream was away, or
// that the server dropped for a slow reader, is never sent again), and a snapshot or an event that cannot be read, or a
// snapshot that does not come, leaves them unknown: neither the old list nor "none" is shown for it.
public sealed partial class ServerActivity
{
    /// <summary>
    /// The most runs the server's snapshot holds (<c>maxRealtimeScanSnapshotRuns</c>): a snapshot of that many may
    /// leave some out, and the web counts them as "at least".
    /// </summary>
    public const int ScanSnapshotLimit = 500;

    /// <summary>
    /// How long a connection that was given the scans may take to send their snapshot, which the server writes right
    /// after its answer to the subscription. After it the scans count as not known.
    /// </summary>
    public static readonly TimeSpan ScansSnapshotWait = TimeSpan.FromSeconds(10);

    // The libraries asked for by name since the names were last unknown: a scan of a library that is in no list asks once.
    private readonly HashSet<long> _askedLibraries = [];

    // This connection's snapshot of the scans has not come yet.
    private bool _awaitsScans;

    private Reread LibrariesSource() => Source(EventsChannels.Scans, "libraries",
        async (server, cancellationToken) =>
        {
            var libraries = (await _admin.ListLibrariesAsync(server, cancellationToken).ConfigureAwait(false)).Items;
            var names = libraries.Where(library => library.Id.Length > 0).DistinctBy(library => library.Id, StringComparer.Ordinal)
                .ToFrozenDictionary(library => library.Id, library => library.Name, StringComparer.Ordinal);
            return state => state with { LibraryNames = names };
        },
        state => state with { LibraryNames = null });

    // A connection that was given the scans sends their snapshot next: what is known so far stays until it comes, but
    // not for longer than ScansSnapshotWait. Without the channel the scans are gone. So is a "there may be more" with no
    // scan known to go with it: it was the last connection's, and this one says anew what it knows.
    private void ScansSubscribedLocked(bool given)
    {
        _awaitsScans = given;
        _scansOverdue.Change(given ? ScansSnapshotWait : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (!given || _state.Scans is null)
        {
            _state = _state with { Scans = null, ScansMayBeMore = false };
        }
    }

    // True when the state changed.
    private bool ScansSnapshotLocked(EventsFrame frame)
    {
        _awaitsScans = false;
        _scansOverdue.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        // A snapshot too large to take, or that is no list of runs, says only that nobody knows the scans now.
        if (frame.IsCut || Payload(frame.Data, AdminJsonContext.Default.ListAdminScanRun) is not { } runs)
        {
            _logger.LogInformation("The scans' snapshot could not be read ({Reason}): the activity indicator does not know the scans",
                frame.IsCut ? "too large" : "not a list of runs");
            return ScansUnknownLocked();
        }
        _state = _state with { Scans = runs, ScansMayBeMore = runs.Count >= ScanSnapshotLimit };
        AskForLibrariesLocked(runs);
        return true;
    }

    private void OnScansOverdue()
    {
        lock (_gate)
        {
            if (!_awaitsScans || !_state.Channels.Contains(EventsChannels.Scans))
            {
                return;
            }
            _awaitsScans = false;
            // Scans that ran when the last connection ended may still run, and their end is never sent again: there may
            // be some. With none known to run, a snapshot that does not come is no sign of a scan, and must not keep
            // the indicator up on a server that sends none.
            bool mayRun = _state.Scans?.Any(scan => scan.IsActive) == true;
            if (mayRun ? !ScansUnknownLocked() : !ScansForgottenLocked())
            {
                return;
            }
        }
        _logger.LogInformation("The scans' snapshot did not come: the activity indicator does not know the scans");
        Notify();
    }

    // The scans are not known and there may be some: the runs the stream tells of from now on are listed as the least
    // there is. True when the state changed.
    private bool ScansUnknownLocked()
    {
        if (_state.Scans is null && _state.ScansMayBeMore)
        {
            return false;
        }
        _state = _state with { Scans = null, ScansMayBeMore = true };
        return true;
    }

    // The scans are not known, and nothing says there are any. True when the state changed.
    private bool ScansForgottenLocked()
    {
        if (_state.Scans is null && !_state.ScansMayBeMore)
        {
            return false;
        }
        _state = _state with { Scans = null, ScansMayBeMore = false };
        return true;
    }

    // True when the state changed.
    private bool ScanEventLocked(EventsFrame frame)
    {
        // Every scans event brings a run. One too large to take, or that is no run, may have begun or ended a scan
        // unheard: as for a snapshot that cannot be read, nobody knows the scans now.
        if (frame.IsCut || Payload(frame.Data, AdminJsonContext.Default.AdminScanRun) is not { Id.Length: > 0 } run)
        {
            if (!ScansUnknownLocked())
            {
                return false;
            }
            _logger.LogInformation("A scan's event could not be read ({Reason}): the activity indicator does not know the scans",
                frame.IsCut ? "too large" : "not a run");
            return true;
        }
        var scans = _state.Scans ?? [];
        int index = -1;
        for (int at = 0; at < scans.Count && index < 0; at++)
        {
            index = scans[at].Id == run.Id ? at : -1;
        }
        // News of a run that is neither known nor active changes nothing, but the first news makes the list known.
        if (index < 0 && !run.IsActive && _state.Scans is not null)
        {
            return false;
        }
        _state = _state with
        {
            Scans = index < 0 ? (run.IsActive ? [.. scans, run] : scans)
                : run.IsActive ? [.. scans.Take(index), run, .. scans.Skip(index + 1)]
                : [.. scans.Take(index), .. scans.Skip(index + 1)],
        };
        if (run.IsActive)
        {
            AskForLibrariesLocked([run]);
        }
        return true;
    }

    // A library the names do not hold (made since they were read) is asked for once; while the names are still on their
    // way, their answer brings it.
    private void AskForLibrariesLocked(IReadOnlyList<AdminScanRun> runs)
    {
        if (_state.LibraryNames is not { } names)
        {
            return;
        }
        bool ask = false;
        foreach (var run in runs)
        {
            ask |= !names.ContainsKey(run.LibraryId.ToString(CultureInfo.InvariantCulture)) && _askedLibraries.Add(run.LibraryId);
        }
        if (ask)
        {
            CueLocked(_libraries);
        }
    }

    // What is no run (or no list of them) is left alone.
    private T? Payload<T>(JsonElement data, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        if (data.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            return null;
        }
        try
        {
            return data.Deserialize(type);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug("A frame of the events stream could not be read: {Failure}", ex.GetType().Name);
            return null;
        }
    }
}
