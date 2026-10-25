namespace Granary.Core.Playback;

/// <summary>
/// The viewer's state on the server changed through this app: a watched mark, a favourite, the watchlist, a card taken
/// off Home, or playback (where the viewer left off, Continue Watching, Next Up). Rows built from that state are stale
/// after it. Silo's web client learns this from the server's events; this app tells itself.
/// </summary>
/// <remarks>
/// A page that is away compares <see cref="Version"/> with the one it loaded at when it shows again, so it need not
/// follow <see cref="Changed"/> (and stay referenced by it) while it is not shown.
/// </remarks>
public sealed class ViewerStateChanges
{
    private long _version;

    public ViewerStateChanges(PlaybackStops stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        // A stop carries where the viewer left off: Continue Watching and Next Up change with it.
        stops.Stopped += (_, _) => Report();
    }

    /// <summary>Counts the changes so far.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Raised after every change, on whichever thread made it.</summary>
    public event EventHandler? Changed;

    public void Report()
    {
        Interlocked.Increment(ref _version);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
