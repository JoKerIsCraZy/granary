using System.Globalization;
using System.Text.RegularExpressions;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels.Player;

/// <summary>
/// Track wording as Silo's web player has it (web/src/lib/mediaFormat.ts, web/src/lib/languageNames.ts,
/// web/src/pages/ItemDetail/components/versionFormatUtils.ts). Chips are upper-cased where they are shown, not here.
/// </summary>
public static partial class TrackText
{
    // Codec families that cannot carry Atmos: a compatibility track of one of them is often titled after its source
    // ("AC3 5.1 (from TrueHD Atmos)"), so its titles say nothing. E-AC-3 contains "ac-3" and is checked first.
    private static readonly string[] NonAtmosCodecs = ["aac", "ac3", "ac-3", "dts", "flac", "mp3", "opus", "vorbis", "pcm", "alac"];

    /// <summary>
    /// The row title of an audio track: its own title, else the embedded one, else language, codec and channels as
    /// the file has them (web: audioTitle).
    /// </summary>
    public static string AudioTitle(WatchAudioTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return NonBlank(track.Title) ?? NonBlank(track.EmbeddedTitle)
            ?? NonBlank(Join(" ", track.Language?.Trim(), track.Codec?.Trim(), Channels(track.Channels)))
            ?? "Audio";
    }

    /// <summary>"German · 5.1(side) · 640 kbps · 48,000 Hz · 24-bit"; the language only when the title does not say it already.</summary>
    public static string AudioMeta(WatchAudioTrack track, string title, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        string language = LanguageName(track.Language);
        return Join(" · ",
            string.Equals(language, title, StringComparison.OrdinalIgnoreCase) ? null : language,
            track.Layout?.Trim(),
            track.Bitrate > 0 ? string.Format(culture ?? CultureInfo.CurrentCulture, "{0:N0} kbps", track.Bitrate) : null,
            track.SampleRate > 0 ? string.Format(culture ?? CultureInfo.CurrentCulture, "{0:N0} Hz", track.SampleRate) : null,
            track.BitDepth > 0 ? string.Create(CultureInfo.InvariantCulture, $"{track.BitDepth}-bit") : null);
    }

    /// <summary>The audio family: "DD+ Atmos", "TrueHD", "EAC3", "AC3"; empty without a codec (web: audioTrackLabel).</summary>
    public static string AudioCodec(WatchAudioTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (string.IsNullOrWhiteSpace(track.Codec))
        {
            return "";
        }
        string codec = track.Codec.Trim();
        string lower = codec.ToLowerInvariant();
        bool eac3 = lower.Contains("eac3", StringComparison.Ordinal) || lower.Contains("e-ac-3", StringComparison.Ordinal);
        if (lower.Contains("atmos", StringComparison.Ordinal) || (MentionsAtmos(track) && CanCarryAtmos(lower)))
        {
            return lower.Contains("truehd", StringComparison.Ordinal) ? "TrueHD Atmos" : eac3 ? "DD+ Atmos" : "Atmos";
        }
        if (lower.Contains("truehd", StringComparison.Ordinal))
        {
            return "TrueHD";
        }
        if (lower.Contains("dts-hd", StringComparison.Ordinal) || lower.Contains("dts:x", StringComparison.Ordinal))
        {
            return "DTS-HD";
        }
        if (lower.Contains("dts", StringComparison.Ordinal))
        {
            return "DTS";
        }
        return eac3 ? "EAC3"
            : lower.Contains("aac", StringComparison.Ordinal) ? "AAC"
            : lower.Contains("flac", StringComparison.Ordinal) ? "FLAC"
            : codec.ToUpperInvariant();
    }

    /// <summary>A subtitle row's title: its language's name, "Unknown" without one (web: getLanguageName).</summary>
    public static string SubtitleLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) ? "Unknown" : LanguageName(language);

    /// <summary>"ASS", "SRT", "VTT", "PGS", "DVD", "DVB"; empty for any other codec (web: getSubtitleFormatLabel).</summary>
    public static string SubtitleFormat(string? codec) => codec?.Trim().ToLowerInvariant() switch
    {
        "ass" or "ssa" => "ASS",
        "srt" or "subrip" => "SRT",
        "vtt" or "webvtt" => "VTT",
        "pgs" or "hdmv_pgs_subtitle" => "PGS",
        "dvd_subtitle" => "DVD",
        "dvb_subtitle" => "DVB",
        _ => "",
    };

    /// <summary>"External", "Downloaded", else "Embedded".</summary>
    public static string SubtitleSource(string? source) => source switch
    {
        SubtitleSources.External => "External",
        SubtitleSources.Downloaded => "Downloaded",
        _ => "Embedded",
    };

    /// <summary>A track's own label under its row, unless it only repeats the language or the format; empty then.</summary>
    public static string SubtitleDetail(string? label, string? language, string languageName, string? codec)
    {
        string text = label?.Trim() ?? "";
        if (text.Length == 0 || text == language || text == languageName)
        {
            return "";
        }
        string plain = FormatName(text);
        return plain.Length > 0 && (plain == FormatName(codec) || plain == FormatName(SubtitleFormat(codec))) ? "" : text;
    }

    private static string FormatName(string? value) =>
        new((value ?? "").ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());

    /// <summary>"7.1", "5.1", "stereo", "3 ch"; empty when unknown (web: formatChannels).</summary>
    public static string Channels(int channels) => channels switch
    {
        <= 0 => "",
        8 => "7.1",
        6 => "5.1",
        2 => "stereo",
        _ => string.Create(CultureInfo.InvariantCulture, $"{channels} ch"),
    };

    /// <summary>
    /// The English name of a track language: "German" for <c>de</c>, <c>ger</c> or <c>deu</c>, "Portuguese (Brazil)" for
    /// <c>pt-BR</c>; a name older files carry as it is; "Unknown language (xx)" for a code nobody knows.
    /// </summary>
    public static string LanguageName(string? language)
    {
        string tag = language?.Trim().Replace('_', '-') ?? "";
        if (tag.Length == 0)
        {
            return "";
        }
        if (EnglishName(tag) is { } name)
        {
            return name;
        }
        if (tag.Length > 3)
        {
            // "english", "Brazilian Portuguese": a name, not a code.
            return string.Join(' ', tag.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries)
                .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
        }
        return $"Unknown language ({tag})";
    }

    private static string? EnglishName(string tag)
    {
        string[] parts = tag.Split('-');
        string primary = parts[0].ToLowerInvariant();
        if (primary == "und")
        {
            return "Unknown language";
        }
        if (TrackLanguages.Culture(primary) is not { } language)
        {
            return null;
        }
        if (parts.Length > 1)
        {
            try
            {
                // ICU names the variant as CLDR does: "Portuguese (Brazil)", "Serbian (Latin)".
                return CultureInfo.GetCultureInfo(language.TwoLetterISOLanguageName + "-" + string.Join('-', parts[1..]), predefinedOnly: true).EnglishName;
            }
            catch (CultureNotFoundException)
            {
                // An unknown region or script: the language alone.
            }
        }
        return language.EnglishName;
    }

    // The profile and the titles can name Atmos too; some releases only say so in the title.
    private static bool MentionsAtmos(WatchAudioTrack track) =>
        AtmosWord().IsMatch(Join(" ", track.Profile, track.Title, track.EmbeddedTitle));

    private static bool CanCarryAtmos(string lowerCodec) =>
        lowerCodec.Contains("truehd", StringComparison.Ordinal) || lowerCodec.Contains("eac3", StringComparison.Ordinal)
        || lowerCodec.Contains("e-ac-3", StringComparison.Ordinal) || !NonAtmosCodecs.Any(family => lowerCodec.Contains(family, StringComparison.Ordinal));

    private static string Join(string separator, params string?[] parts) =>
        string.Join(separator, parts.Where(part => !string.IsNullOrEmpty(part)));

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"\batmos\b", RegexOptions.IgnoreCase)]
    private static partial Regex AtmosWord();
}
