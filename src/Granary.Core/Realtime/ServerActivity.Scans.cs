using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Granary.Core.Api.Admin;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Realtime;

// Library scans exist only on the stream: the server has no list of the active ones to read. As the web keeps them
// (RealtimeEventsProvider.tsx, hydrateScans and applyScanUpdate): the channel's snapshot replaces what is known, and each
// scan.* event brings one run, which takes its place, joins the list, or leaves it once it is no longer queued or running.
public sealed partial class ServerActivity
{
    /// <summary>
    /// The most runs the server's snapshot holds (<c>maxRealtimeScanSnapshotRuns</c>): with that many known, there may be
    /// more, and the web counts them as "at least".
    /// </summary>
    public const int ScanSnapshotLimit = 500;

    // The libraries asked for by name since the names were last unknown: a scan of a library that is in no list asks once.
    private readonly HashSet<long> _askedLibraries = [];

    private Reread LibrariesSource() => Source(EventsChannels.Scans, "libraries",
        async (server, cancellationToken) =>
        {
            var libraries = (await _admin.ListLibrariesAsync(server, cancellationToken).ConfigureAwait(false)).Items;
            var names = libraries.Where(library => library.Id.Length > 0).DistinctBy(library => library.Id, StringComparer.Ordinal)
                .ToFrozenDictionary(library => library.Id, library => library.Name, StringComparer.Ordinal);
            return state => state with { LibraryNames = names };
        },
        state => state with { LibraryNames = null });

    // True when the state changed.
    private bool ScansSnapshotLocked(EventsFrame frame)
    {
        // A snapshot too large to take says nothing of its runs: what was known stays.
        if (frame.IsCut || Payload(frame.Data, AdminJsonContext.Default.ListAdminScanRun) is not { } runs)
        {
            return false;
        }
        _state = _state with { Scans = runs };
        AskForLibrariesLocked(runs);
        return true;
    }

    private bool ScanEventLocked(EventsFrame frame)
    {
        if (frame.IsCut || Payload(frame.Data, AdminJsonContext.Default.AdminScanRun) is not { Id.Length: > 0 } run)
        {
            return false;
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

    // What is no run (or no list of them) is left alone: the next snapshot puts the list right.
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
