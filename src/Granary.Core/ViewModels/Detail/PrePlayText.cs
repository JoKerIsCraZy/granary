using System.Globalization;
using System.Text.RegularExpressions;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Playback;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Player;

namespace Granary.Core.ViewModels.Detail;

/// <summary>
/// The words of the detail page's Version, Audio and Subs pickers, as Silo's web detail page has them
/// (web/src/pages/ItemDetail/components/VersionFlyout.tsx, versionFormatUtils.ts, prePlaySelection.ts).
/// </summary>
public static partial class PrePlayText
{
    private const double Kilobyte = 1024d;
    private const double Megabyte = Kilobyte * 1024;
    private const double Gigabyte = Megabyte * 1024;

    /// <summary>How high a resolution ranks; -1 for one the web does not know (web: resolutionScore).</summary>
    public static int ResolutionScore(string? resolution) => resolution?.ToLowerInvariant() switch
    {
        "4k" or "2160p" => 4,
        "1440p" => 3,
        "1080p" => 2,
        "720p" => 1,
        "480p" => 0,
        _ => -1,
    };

    /// <summary>The highest resolution first; equal ones keep the server's order (web: sortByResolution).</summary>
    public static IReadOnlyList<FileVersion> SortByResolution(IEnumerable<FileVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        return [.. versions.OrderByDescending(v => ResolutionScore(v.Resolution))];
    }

    /// <summary>"2160p · HEVC · HDR10 · DTS"; the container alone when the file says nothing else (web: buildQualitySummary).</summary>
    public static string QualitySummary(FileVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var parts = new List<string?>
        {
            version.Resolution,
            version.CodecVideo.ToUpperInvariant(),
            PlaybackInfo.VideoRangeLabel(version.VideoTracks, version.Hdr),
            AudioLabel(version),
        };
        parts.RemoveAll(string.IsNullOrEmpty);
        if (parts.Count == 0 && version.Container.Length > 0)
        {
            parts.Add(version.Container.ToUpperInvariant());
        }
        return string.Join(" · ", parts);
    }

    /// <summary>"38.9 GB · Remux": the size and the source the release name gives (web: buildDetailLine).</summary>
    public static string DetailLine(FileVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return Join(FileSize(version.FileSize), SourceHint(version.FileName));
    }

    /// <summary>The closed picker's words: the quality, else the size and source, else the file's number (web: buildVersionTriggerSummary).</summary>
    public static string VersionSummary(FileVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return QualitySummary(version) is { Length: > 0 } quality ? quality
            : DetailLine(version) is { Length: > 0 } detail ? detail
            : FileIdFallback(version);
    }

    /// <summary>"Version 12": a file the server says nothing else about, by its id (web: versions.fileIdFallback).</summary>
    public static string FileIdFallback(FileVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return AppText.Get("web.detail.versions.fileIdFallback", ("id", version.FileId));
    }

    /// <summary>"38.9 GB", "700.0 MB": 1024-based with one decimal; empty for none (web: formatFileSize).</summary>
    public static string FileSize(long bytes) => bytes switch
    {
        <= 0 => "",
        >= (long)Gigabyte => string.Create(CultureInfo.InvariantCulture, $"{bytes / Gigabyte:0.0} GB"),
        >= (long)Megabyte => string.Create(CultureInfo.InvariantCulture, $"{bytes / Megabyte:0.0} MB"),
        >= (long)Kilobyte => string.Create(CultureInfo.InvariantCulture, $"{bytes / Kilobyte:0.0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
    };

    /// <summary>The source a release name tells: "Remux", "WEB-DL", "BluRay", …; null when it tells none (web: extractSourceHint).</summary>
    public static string? SourceHint(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }
        return Remux().IsMatch(fileName) ? "Remux"
            : WebDl().IsMatch(fileName) ? "WEB-DL"
            : WebRip().IsMatch(fileName) ? "WEBRip"
            : BluRay().IsMatch(fileName) ? "BluRay"
            : BdRip().IsMatch(fileName) ? "BDRip"
            : Hdtv().IsMatch(fileName) ? "HDTV"
            : DvdRip().IsMatch(fileName) ? "DVDRip"
            : null;
    }

    /// <summary>"German · TrueHD Atmos · 7.1"; "Unknown" without a track (web: formatAudioTrackSummary).</summary>
    public static string AudioSummary(WatchAudioTrack? track) => track is null
        ? AppText.Get("web.detail.audio.unknown")
        : Join(TrackText.SubtitleLanguage(track.Language), TrackText.AudioCodec(track), TrackText.Channels(track.Channels));

    /// <summary>
    /// The closed Subs picker's words for a track: its language, "(SDH)" and "(Forced)" when the name does not say so, and
    /// the format, "English (SDH) · SRT" (web: formatSubtitlePillSummary, which puts its markers after the name the same way).
    /// </summary>
    public static string SubtitleSummary(string languageLabel, string? label, string? codec, bool forced, bool hearingImpaired)
    {
        string name = !string.IsNullOrWhiteSpace(languageLabel) ? languageLabel.Trim()
            : !string.IsNullOrWhiteSpace(label) ? label.Trim()
            : AppText.Get("web.detail.subtitles.unknown");
        string text = name;
        // The name is the track's own or a language's: the words it may already hold are the file's, in English.
        if (hearingImpaired && !HearingImpairedWord().IsMatch(name))
        {
            text += " " + AppText.Get("web.detail.subtitles.sdhMarker");
        }
        if (forced && !name.Contains("forced", StringComparison.OrdinalIgnoreCase))
        {
            text += " " + AppText.Get("web.detail.subtitles.forcedMarker");
        }
        string format = SubtitleFormat(codec);
        return format.Length > 0 ? $"{text} · {format}" : text;
    }

    /// <summary>"SRT", "PGS", or the codec itself in capitals (web: getSubtitleFormatLabel, else the codec upper-cased).</summary>
    public static string SubtitleFormat(string? codec) =>
        TrackText.SubtitleFormat(codec) is { Length: > 0 } format ? format : codec?.Trim().ToUpperInvariant() ?? "";

    // The file's main audio as its quality line and its badge name it: the first track's profile and titles can tell Atmos
    // when its codec is the file's (web: mapAudioLabel with versionAudioAtmosEvidence).
    internal static string AudioLabel(FileVersion version)
    {
        if (version.CodecAudio.Length == 0)
        {
            return "";
        }
        var first = version.AudioTracks.FirstOrDefault();
        bool same = first is not null && string.Equals(first.Codec, version.CodecAudio, StringComparison.OrdinalIgnoreCase);
        return TrackText.AudioCodec(new WatchAudioTrack
        {
            Codec = version.CodecAudio,
            Profile = same ? first!.Profile : null,
            Title = same ? first!.Title : null,
            EmbeddedTitle = same ? first!.EmbeddedTitle : null,
        });
    }

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    [GeneratedRegex(@"\bremux\b", RegexOptions.IgnoreCase)]
    private static partial Regex Remux();

    [GeneratedRegex(@"\bweb-dl\b", RegexOptions.IgnoreCase)]
    private static partial Regex WebDl();

    [GeneratedRegex(@"\bwebrip\b", RegexOptions.IgnoreCase)]
    private static partial Regex WebRip();

    [GeneratedRegex(@"\bbluray\b", RegexOptions.IgnoreCase)]
    private static partial Regex BluRay();

    [GeneratedRegex(@"\bbdrip\b", RegexOptions.IgnoreCase)]
    private static partial Regex BdRip();

    [GeneratedRegex(@"\bhdtv\b", RegexOptions.IgnoreCase)]
    private static partial Regex Hdtv();

    [GeneratedRegex(@"\bdvdrip\b", RegexOptions.IgnoreCase)]
    private static partial Regex DvdRip();

    [GeneratedRegex(@"\b(?:sdh|cc|hi)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HearingImpairedWord();
}
