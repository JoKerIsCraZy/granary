using System.Globalization;
using Granary.Core.Playback;

namespace Granary.Player;

/// <summary>
/// The read-ahead buffer (<see cref="ReadAheadBuffer"/>) as mpv's cache options, given to each title on its own. They are
/// not Granary's kept options: the viewer's mpv.conf wins, so an option it names anywhere, in a profile or an include too,
/// is left to it. <c>demuxer-hysteresis-secs</c> stays at mpv's 0: mpv then tops the cache up all the time while it plays,
/// not in bursts, so the connection never sits idle long enough for a proxy before the server to close it, which is what
/// makes a large buffer safe.
/// </summary>
internal static class MpvReadAhead
{
    /// <summary>The options the buffer sets, which a mpv.conf may set instead.</summary>
    public static IReadOnlySet<string> Names { get; } =
        new HashSet<string>(["cache", "demuxer-max-bytes", "demuxer-max-back-bytes", "demuxer-readahead-secs"], StringComparer.Ordinal);

    /// <summary>The options for one title with <paramref name="buffer"/>, but those the viewer's mpv.conf names (<paramref name="fileSets"/>).</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Options(ReadAheadBuffer buffer, IReadOnlySet<string> fileSets)
    {
        ArgumentNullException.ThrowIfNull(fileSets);
        // A file that sets the cache itself (cache=no keeps memory and delay low) has the whole buffer left to it, sizes too.
        if (fileSets.Contains("cache"))
        {
            return [];
        }
        var (ahead, behind) = ReadAheadBuffers.Bytes(buffer);
        // Byte counts, not mpv's own size suffixes.
        KeyValuePair<string, string>[] all =
        [
            new("cache", "yes"),
            new("demuxer-max-bytes", ahead.ToString(CultureInfo.InvariantCulture)),
            new("demuxer-max-back-bytes", behind.ToString(CultureInfo.InvariantCulture)),
            new("demuxer-readahead-secs", ReadAheadBuffers.AheadSeconds.ToString(CultureInfo.InvariantCulture)),
        ];
        return [.. all.Where(o => !fileSets.Contains(o.Key))];
    }
}
