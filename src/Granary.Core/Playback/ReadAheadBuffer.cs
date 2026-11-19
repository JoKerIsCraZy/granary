namespace Granary.Core.Playback;

/// <summary>
/// How much of a title the player reads ahead of the picture, and keeps behind it for a step back (Settings → Playback →
/// This PC). More rides out a slow or unsteady network or NAS, and takes as much more memory while a title plays.
/// </summary>
public enum ReadAheadBuffer
{
    /// <summary>384 MiB ahead, 64 MiB behind.</summary>
    Default,

    /// <summary>150 MiB ahead, 50 MiB behind: the player's own sizes before this setting.</summary>
    Mb150,

    /// <summary>768 MiB ahead, 128 MiB behind.</summary>
    Mb768,

    /// <summary>1 GiB ahead, 192 MiB behind.</summary>
    Gb1,

    /// <summary>2 GiB ahead, 256 MiB behind.</summary>
    Gb2,
}

/// <summary>The sizes of each <see cref="ReadAheadBuffer"/>, and its name in <c>playback-device.json</c>.</summary>
public static class ReadAheadBuffers
{
    private const long MiB = 1024 * 1024;

    /// <summary>
    /// mpv's <c>demuxer-readahead-secs</c>. With the cache on, mpv's <c>cache-secs</c> stands over it and is very high by
    /// default, so the size ahead is what limits the read, not this.
    /// </summary>
    public const int AheadSeconds = 120;

    /// <summary>Every choice, in the order Settings lists them.</summary>
    public static IReadOnlyList<ReadAheadBuffer> All { get; } =
        [ReadAheadBuffer.Default, ReadAheadBuffer.Mb150, ReadAheadBuffer.Mb768, ReadAheadBuffer.Gb1, ReadAheadBuffer.Gb2];

    /// <summary>How many bytes the player reads ahead, and how many it keeps behind.</summary>
    public static (long AheadBytes, long BehindBytes) Bytes(ReadAheadBuffer buffer) => buffer switch
    {
        ReadAheadBuffer.Mb150 => (150 * MiB, 50 * MiB),
        ReadAheadBuffer.Mb768 => (768 * MiB, 128 * MiB),
        ReadAheadBuffer.Gb1 => (1024 * MiB, 192 * MiB),
        ReadAheadBuffer.Gb2 => (2048 * MiB, 256 * MiB),
        _ => (384 * MiB, 64 * MiB),
    };

    /// <summary>The choice's name in <c>playback-device.json</c>.</summary>
    public static string Name(ReadAheadBuffer buffer) => buffer switch
    {
        ReadAheadBuffer.Mb150 => "150mb",
        ReadAheadBuffer.Mb768 => "768mb",
        ReadAheadBuffer.Gb1 => "1gb",
        ReadAheadBuffer.Gb2 => "2gb",
        _ => "default",
    };

    /// <summary>The choice a name stands for; <see cref="ReadAheadBuffer.Default"/> for none or one Granary does not know.</summary>
    public static ReadAheadBuffer FromName(string? name) =>
        All.FirstOrDefault(buffer => string.Equals(Name(buffer), name?.Trim(), StringComparison.OrdinalIgnoreCase));
}
