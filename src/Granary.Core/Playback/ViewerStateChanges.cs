namespace Granary.Core.Playback;

/// <summary>
/// The viewer's state on the server changed: a watched mark, a favourite, the watchlist, a card taken off Home, or
/// playback (where the viewer left off, Continue Watching, Next Up). Rows built from that state are stale after it. This
/// app tells itself of its own changes; the server's events (<see cref="Realtime.RealtimeEvents"/>) tell it of another
/// device's, and of titles the catalog gained or changed (<see cref="ViewerStateChangedEventArgs.IsCatalogOnly"/>).
/// </summary>
/// <remarks>
/// A page that is away compares <see cref="Version"/> with the one it loaded at when it shows again, so it need not
/// follow <see cref="Changed"/> (and stay referenced by it) while it is not shown.
/// </remarks>
public sealed class ViewerStateChanges
{
    private readonly PlaybackStops _stops;
    private long _version;

    public ViewerStateChanges(PlaybackStops stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        _stops = stops;
        // A stop carries where the viewer left off: Continue Watching and Next Up change with it.
        stops.Stopped += (_, _) => Report();
    }

    /// <summary>Counts the changes so far, the catalog's included.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// A playback of this app runs: the player is up, full or minimized. A page under it can leave what changes meanwhile
    /// until the player is gone, since the playback's stop is a change too, raised once it ended.
    /// </summary>
    public bool IsPlaying => _stops.IsPlaying;

    /// <summary>Raised after every change, on whichever thread made it.</summary>
    public event EventHandler<ViewerStateChangedEventArgs>? Changed;

    /// <summary>The viewer's own state changed: a mark, a favourite, the watchlist, a rating, progress or a stop.</summary>
    public void Report() => Raise(ViewerStateChangedEventArgs.Viewer);

    /// <summary>Only the catalog changed (titles added or changed, the profile's access): the viewer's marks and progress did not.</summary>
    public void ReportCatalog() => Raise(ViewerStateChangedEventArgs.Catalog);

    private void Raise(ViewerStateChangedEventArgs change)
    {
        Interlocked.Increment(ref _version);
        Changed?.Invoke(this, change);
    }
}

/// <summary>What a <see cref="ViewerStateChanges.Changed"/> was about.</summary>
public sealed class ViewerStateChangedEventArgs : EventArgs
{
    public static ViewerStateChangedEventArgs Viewer { get; } = new(isCatalogOnly: false);

    public static ViewerStateChangedEventArgs Catalog { get; } = new(isCatalogOnly: true);

    private ViewerStateChangedEventArgs(bool isCatalogOnly) => IsCatalogOnly = isCatalogOnly;

    /// <summary>
    /// Only the catalog changed: rows of titles are stale, while what shows the viewer's own state (where Play resumes,
    /// the progress) is not.
    /// </summary>
    public bool IsCatalogOnly { get; }
}
