using Granary.Core.Api.Downloads;

namespace Granary.Core.Downloads;

/// <summary>
/// The series monitors of each server, account and profile as Granary last read or wrote them (<see cref="SeriesMonitors"/>),
/// and the titles of their series as Granary knows them. The queue reads the storage limits here, the pages the monitors; it
/// holds them for the run, not on disk: a run at the start reads them again. Until a run read a scope's list, or gave up on it
/// (<see cref="IsKnown"/>), whether a limit holds that scope's episodes is not known, which the queue tells apart from "not
/// monitored". Safe from any thread; <see cref="Changed"/> comes on the thread that changed them.
/// </summary>
public sealed class MonitorBook
{
    private readonly Lock _gate = new();
    private readonly Dictionary<DownloadScope, IReadOnlyList<DownloadSubscription>> _monitors = [];
    private readonly HashSet<DownloadScope> _known = [];
    private readonly Dictionary<(DownloadScope Scope, string SeriesId), string> _titles = [];

    /// <summary>Raised after the monitors of any scope changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The scope's monitors, newest first as the server lists them; none before a read.</summary>
    public IReadOnlyList<DownloadSubscription> In(DownloadScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            return _monitors.GetValueOrDefault(scope) ?? [];
        }
    }

    /// <summary>
    /// A run read the scope's monitor list whole, or gave up on it (the server offers no downloads, or did not answer a few runs
    /// in a row): its monitors are as known as they get. False before, as at Granary's start; a create or a stop alone does not
    /// make it true, as the scope's other monitors are not known from it.
    /// </summary>
    public bool IsKnown(DownloadScope scope)
    {
        lock (_gate)
        {
            return _known.Contains(scope);
        }
    }

    /// <summary>A run gave up on the scope's list: the monitors it had stand, and are as known as they get for now.</summary>
    internal void NoteUnread(DownloadScope scope) => Change(scope, list => list, known: true);

    /// <summary>The scope's monitor of <paramref name="seriesId"/>, paused or not; null when the series is not monitored.</summary>
    public DownloadSubscription? ForSeries(DownloadScope scope, string seriesId) =>
        In(scope).FirstOrDefault(m => m.SeriesId == seriesId);

    /// <summary>The scope's monitor with this id.</summary>
    public DownloadSubscription? Find(DownloadScope scope, string subscriptionId) =>
        In(scope).FirstOrDefault(m => m.Id == subscriptionId);

    /// <summary>The series' title as a download dialog or a download of it named it; null when none did.</summary>
    public string? TitleOf(DownloadScope scope, string seriesId)
    {
        lock (_gate)
        {
            return _titles.GetValueOrDefault((scope, seriesId));
        }
    }

    internal void NoteTitle(DownloadScope scope, string seriesId, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }
        lock (_gate)
        {
            _titles[(scope, seriesId)] = title.Trim();
        }
    }

    /// <summary>The scope's monitors as a whole list read them.</summary>
    internal void Replace(DownloadScope scope, IReadOnlyList<DownloadSubscription> monitors) => Change(scope, _ => [.. monitors], known: true);

    /// <summary>A monitor as the server answered it: it takes the place of the one with its id, or comes first.</summary>
    internal void Put(DownloadScope scope, DownloadSubscription monitor) =>
        Change(scope, list => list.Any(m => m.Id == monitor.Id) ? [.. list.Select(m => m.Id == monitor.Id ? monitor : m)] : [monitor, .. list], known: false);

    internal void Remove(DownloadScope scope, string subscriptionId) => Change(scope, list => [.. list.Where(m => m.Id != subscriptionId)], known: false);

    // A whole list read, or a run giving up on it, leaves the scope known; one monitor written does not.
    private void Change(DownloadScope scope, Func<IReadOnlyList<DownloadSubscription>, IReadOnlyList<DownloadSubscription>> change, bool known)
    {
        bool changed;
        lock (_gate)
        {
            var before = _monitors.GetValueOrDefault(scope) ?? [];
            var after = change(before);
            // A monitor's validator changes with every change to it: the same ids and validators in the same order are the same.
            changed = !before.Select(m => (m.Id, m.Etag)).SequenceEqual(after.Select(m => (m.Id, m.Etag)));
            changed |= known && _known.Add(scope);
            _monitors[scope] = after;
        }
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
