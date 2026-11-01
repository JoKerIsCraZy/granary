using System.Collections;

namespace Granary.Core.Playback;

/// <summary>A stretch of the media the player holds, from <see cref="StartSeconds"/> to <see cref="EndSeconds"/>.</summary>
public readonly record struct BufferedRange(double StartSeconds, double EndSeconds);

/// <summary>
/// What the player holds of the media: the stretches it can play and seek within without reading the stream again, as
/// a video element's buffered ranges are on the web. In start order, none empty or overlapping; immutable and compared
/// by what they hold.
/// </summary>
public sealed class BufferedRanges : IReadOnlyList<BufferedRange>, IEquatable<BufferedRanges>
{
    private readonly BufferedRange[] _ranges;

    private BufferedRanges(BufferedRange[] ranges) => _ranges = ranges;

    /// <summary>Nothing held, as for a local file or before the player read anything.</summary>
    public static BufferedRanges None { get; } = new([]);

    public int Count => _ranges.Length;

    public BufferedRange this[int index] => _ranges[index];

    /// <summary>
    /// The stretches of <paramref name="ranges"/>, in any order: those that are empty or not a number are left out, those
    /// that overlap or touch are joined (mpv's overlap for a bit before it joins them itself).
    /// </summary>
    public static BufferedRanges Of(IEnumerable<BufferedRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var valid = ranges.Where(r => double.IsFinite(r.StartSeconds) && double.IsFinite(r.EndSeconds) && r.EndSeconds > r.StartSeconds)
            .OrderBy(r => r.StartSeconds)
            .ToList();
        if (valid.Count == 0)
        {
            return None;
        }
        var joined = new List<BufferedRange>(valid.Count) { valid[0] };
        foreach (var range in valid.Skip(1))
        {
            var last = joined[^1];
            if (range.StartSeconds <= last.EndSeconds)
            {
                joined[^1] = last with { EndSeconds = Math.Max(last.EndSeconds, range.EndSeconds) };
            }
            else
            {
                joined.Add(range);
            }
        }
        return new BufferedRanges([.. joined]);
    }

    /// <summary>
    /// The stretches in the title's time: moved by <paramref name="offsetSeconds"/> as the position is (source = player +
    /// offset) and kept within 0 to <paramref name="durationSeconds"/>. Nothing while the length is unknown.
    /// </summary>
    public BufferedRanges OnTimeline(double offsetSeconds, double durationSeconds)
    {
        if (!double.IsFinite(offsetSeconds) || !double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            return None;
        }
        if (offsetSeconds == 0 && (Count == 0 || (_ranges[0].StartSeconds >= 0 && _ranges[^1].EndSeconds <= durationSeconds)))
        {
            return this;
        }
        var moved = new List<BufferedRange>(Count);
        foreach (var range in _ranges)
        {
            double start = Math.Clamp(range.StartSeconds + offsetSeconds, 0, durationSeconds);
            double end = Math.Clamp(range.EndSeconds + offsetSeconds, 0, durationSeconds);
            if (end > start)
            {
                moved.Add(new BufferedRange(start, end));
            }
        }
        return moved.Count == 0 ? None : new BufferedRanges([.. moved]);
    }

    public bool Equals(BufferedRanges? other) => other is not null && _ranges.AsSpan().SequenceEqual(other._ranges);

    public override bool Equals(object? obj) => Equals(obj as BufferedRanges);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var range in _ranges)
        {
            hash.Add(range);
        }
        return hash.ToHashCode();
    }

    public override string ToString() =>
        "[" + string.Join(", ", _ranges.Select(r => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{r.StartSeconds}-{r.EndSeconds}"))) + "]";

    public IEnumerator<BufferedRange> GetEnumerator() => ((IEnumerable<BufferedRange>)_ranges).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
