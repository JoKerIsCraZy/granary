using Granary.Core.Texts;

namespace Granary.Core.ViewModels;

/// <summary>
/// Sizes, rates and channels as the web's <c>lib/mediaFormat.ts</c> writes them for the item page, the player's
/// playback info and the pickers: the number in the format locale (<see cref="NumberText"/>), the unit in the web's
/// words (<c>web.common.format.*</c>).
/// </summary>
public static class MediaText
{
    private const double Kibi = 1024;

    /// <summary>
    /// "38.9 GB", "700.0 MB", "512 B" (formatFileSize): 1024-based, one decimal; GiB, MiB and KiB with
    /// <paramref name="iecUnits"/>, as the playback info names them; <paramref name="fallback"/> for no size.
    /// </summary>
    public static string FileSize(long bytes, bool iecUnits = false, string fallback = "")
    {
        if (bytes <= 0)
        {
            return fallback;
        }
        return bytes switch
        {
            >= 1L << 30 => AppText.Get(iecUnits ? "web.common.format.size.gib" : "web.common.format.size.gb",
                ("size", NumberText.Decimal(bytes / (Kibi * Kibi * Kibi), 1))),
            >= 1L << 20 => AppText.Get(iecUnits ? "web.common.format.size.mib" : "web.common.format.size.mb",
                ("size", NumberText.Decimal(bytes / (Kibi * Kibi), 1))),
            >= 1L << 10 => AppText.Get(iecUnits ? "web.common.format.size.kib" : "web.common.format.size.kb",
                ("size", NumberText.Decimal(bytes / Kibi, 1))),
            _ => AppText.Get("web.common.format.size.bytes", ("size", bytes)),
        };
    }

    /// <summary>"12,345 kbps" (formatBitrate): whole kbps, grouped; <paramref name="fallback"/> for none.</summary>
    public static string Bitrate(double kbps, string fallback = "") =>
        kbps > 0 && double.IsFinite(kbps)
            ? AppText.Get("web.common.format.bitrate.kbps", ("rate", NumberText.Grouped(Math.Round(kbps, MidpointRounding.AwayFromZero))))
            : fallback;

    /// <summary>"12.3 Mbps" (formatMbpsFromKbps): one decimal; <paramref name="fallback"/> for none.</summary>
    public static string MbpsFromKbps(double kbps, string fallback = "—") =>
        kbps > 0 && double.IsFinite(kbps)
            ? AppText.Get("web.common.format.bitrate.mbps", ("rate", NumberText.Decimal(kbps / 1000, 1)))
            : fallback;

    /// <summary>"48,000 Hz" (formatSampleRate); <paramref name="fallback"/> for none.</summary>
    public static string SampleRate(int hertz, string fallback = "") =>
        hertz > 0 ? AppText.Get("web.common.format.sampleRate", ("rate", NumberText.Grouped(hertz))) : fallback;

    /// <summary>"7.1", "5.1", "stereo", "3 ch" (formatChannels); empty for none.</summary>
    public static string Channels(int channels) => channels switch
    {
        <= 0 => "",
        8 => "7.1",
        6 => "5.1",
        2 => AppText.Get("web.common.format.channels.stereo"),
        _ => AppText.Get("web.common.format.channels.count", ("count", channels), ("count_text", channels)),
    };
}
