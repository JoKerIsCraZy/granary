namespace Granary.Core.Downloads;

/// <summary>
/// How fast a transfer brings its bytes now (docs/architecture.md, "The queue"): the bytes of the last <see cref="Window"/>
/// over the time they took, on the clock the caller reads. None until <see cref="Least"/> of bytes was seen, so a first read
/// does not say a speed it cannot know, and none once no bytes came for <see cref="Stall"/>, as the Apple app drops its rate.
/// Bytes after such a gap, or fewer bytes than before (the part started over), are measured anew. Not thread-safe: the queue
/// keeps one per transfer under its lock.
/// </summary>
public sealed class TransferRate
{
    /// <summary>How far back the speed looks.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    /// <summary>How long bytes have to come before there is a speed.</summary>
    public static readonly TimeSpan Least = TimeSpan.FromSeconds(2);

    /// <summary>No bytes for this long: no speed, and the next bytes start a new measure.</summary>
    public static readonly TimeSpan Stall = TimeSpan.FromSeconds(3);

    // Points closer to the one kept before are not kept: a fast transfer reports every read, hundreds a second.
    private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(250);

    // The points kept, oldest first: the first one at or before the window's start, then about every Spacing; and the latest.
    private readonly List<(DateTimeOffset At, long Bytes)> _points = [];
    private (DateTimeOffset At, long Bytes)? _latest;

    /// <summary>The transfer had <paramref name="bytes"/> at <paramref name="at"/>.</summary>
    public void Add(DateTimeOffset at, long bytes)
    {
        if (_latest is { } latest && (bytes < latest.Bytes || at < latest.At || at - latest.At > Stall))
        {
            _points.Clear();
        }
        if (_points.Count == 0 || at - _points[^1].At >= Spacing)
        {
            _points.Add((at, bytes));
        }
        _latest = (at, bytes);
        while (_points.Count > 1 && _points[1].At <= at - Window)
        {
            _points.RemoveAt(0);
        }
    }

    /// <summary>Bytes a second as of <paramref name="now"/>; null before there is enough to measure, and after a stall.</summary>
    public double? BytesPerSecond(DateTimeOffset now)
    {
        if (_latest is not { } latest || _points.Count == 0 || now - latest.At > Stall)
        {
            return null;
        }
        var first = _points[0];
        var span = latest.At - first.At;
        return span < Least ? null : (latest.Bytes - first.Bytes) / span.TotalSeconds;
    }
}
