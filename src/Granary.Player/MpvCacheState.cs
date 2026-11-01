using System.Text.Json;
using Granary.Core.Playback;

namespace Granary.Player;

/// <summary>
/// Reads mpv's <c>demuxer-cache-state</c> in its string form, the property's node written as compact JSON (mpv's
/// <c>print_node</c>): <c>seekable-ranges</c> lists the stretches the cache holds as <c>{"start": s, "end": s}</c>, in
/// any order, in the time of <c>time-pos</c>.
/// </summary>
internal static class MpvCacheState
{
    /// <summary>mpv keeps at most 10 ranges; more than this many are not its cache state, and are cut off.</summary>
    internal const int MaxRanges = 32;

    /// <summary>The ranges in <paramref name="json"/>; none for anything that is not a cache state, and a range without two times is left out.</summary>
    public static BufferedRanges SeekableRanges(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return BufferedRanges.None;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("seekable-ranges", out var ranges)
                || ranges.ValueKind != JsonValueKind.Array)
            {
                return BufferedRanges.None;
            }
            var held = new List<BufferedRange>(Math.Min(ranges.GetArrayLength(), MaxRanges));
            foreach (var range in ranges.EnumerateArray())
            {
                if (held.Count == MaxRanges)
                {
                    break;
                }
                if (range.ValueKind == JsonValueKind.Object && Seconds(range, "start") is { } start && Seconds(range, "end") is { } end)
                {
                    held.Add(new BufferedRange(start, end));
                }
            }
            return BufferedRanges.Of(held);
        }
        catch (JsonException)
        {
            return BufferedRanges.None;
        }
    }

    // mpv writes a time that is no number ("inf", "nan") in quotes.
    private static double? Seconds(JsonElement range, string name) =>
        range.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double seconds)
            && double.IsFinite(seconds)
            ? seconds
            : null;
}
