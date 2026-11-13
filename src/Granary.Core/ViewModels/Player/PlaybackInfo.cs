using System.Globalization;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Player;

/// <summary>A section of the playback info: "PLAYER", "VIDEO INFO" and so on, with its rows.</summary>
public sealed record PlaybackInfoSection(string Title, IReadOnlyList<PlaybackInfoRow> Rows);

public sealed record PlaybackInfoRow(string Label, string Value);

/// <summary>
/// The playback info overlay of Silo's web player (web/src/player/playback-info.ts), for mpv: what the server chose,
/// what the player measures, and the file that plays. Values it does not know read "—".
/// </summary>
public static class PlaybackInfo
{
    private const string None = "—";

    // web/src/lib/mediaFormat.ts: CODEC_LABELS; any other codec is upper-cased.
    private static readonly Dictionary<string, string> CodecLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["aac"] = "AAC",
        ["ac3"] = "AC3",
        ["av1"] = "AV1",
        ["dts"] = "DTS",
        ["dtshd"] = "DTS-HD",
        ["eac3"] = "EAC3",
        ["flac"] = "FLAC",
        ["h264"] = "H.264",
        ["hevc"] = "HEVC",
        ["mp3"] = "MP3",
        ["opus"] = "Opus",
        ["truehd"] = "TrueHD",
    };

    /// <param name="stream">The server's stream; null for a file played directly.</param>
    /// <param name="playerWidth">The picture's area in pixels.</param>
    public static IReadOnlyList<PlaybackInfoSection> Sections(PlaybackStreamInfo? stream, PlaybackStats stats, int playerWidth, int playerHeight)
    {
        ArgumentNullException.ThrowIfNull(stats);
        List<PlaybackInfoRow> player = [new(AppText.Get("web.player.info.player"), "mpv")];
        if (stream is not null)
        {
            player.Add(new(AppText.Get("web.player.info.playMethod"), PlayMethod(stream.Plan)));
            player.Add(new(AppText.Get("web.player.info.protocol"), stream.Url.Scheme));
            player.Add(new(AppText.Get("web.player.info.streamType"),
                stream.Plan.Stream.Protocol == StreamProtocols.Hls ? "HLS" : AppText.Get("web.player.info.streamTypeProgressive")));
            if (stream.Requested is { } requested && stream.Playing is { } playing && requested.FileId != playing.FileId)
            {
                player.Add(new(AppText.Get("web.player.info.autoSwitchedFrom"), Join(" ", requested.Resolution.Trim(), CodecLabel(requested.CodecVideo, ""), VideoRangeLabel(requested))));
            }
        }
        List<PlaybackInfoSection> sections =
        [
            new(AppText.Get("web.player.info.section.player"), player),
            new(AppText.Get("web.player.info.section.video"),
            [
                new(AppText.Get("web.player.info.playerDimensions"), Dimensions(playerWidth, playerHeight)),
                new(AppText.Get("web.player.info.videoResolution"), Dimensions(stats.VideoWidth, stats.VideoHeight)),
                new(AppText.Get("granary.player.info.decoding"), stats.HardwareDecoder is { Length: > 0 } hwdec and not "no"
                    ? AppText.Get("granary.player.info.decodingHardware", ("decoder", hwdec))
                    : stats.VideoWidth > 0 ? AppText.Get("granary.player.info.decodingSoftware") : None),
                new(AppText.Get("web.player.info.droppedFrames"), FrameCount(stats.DroppedFrames)),
                new(AppText.Get("granary.player.info.decoderDroppedFrames"), FrameCount(stats.DecoderDroppedFrames)),
            ]),
        ];
        if (stream is null)
        {
            return sections;
        }
        sections.Add(new(AppText.Get("web.player.info.section.stream"),
        [
            new(AppText.Get("web.player.info.videoCodec"), Delivered(stream.Plan, stream.Plan.EffectiveRecipe.VideoCodec, StreamMethods.EncodesVideo(stream.Plan))),
            new(AppText.Get("web.player.info.audioCodec"), Delivered(stream.Plan, stream.Plan.EffectiveRecipe.AudioCodec, StreamMethods.EncodesAudio(stream.Plan))),
        ]));
        sections.Add(SourceFile(stream.Playing, stream.AudioOrdinal));
        return sections;
    }

    /// <summary>
    /// How the plan plays, in the web's words (formatDelivery): as the settings gear's dot says it, so a remux that converts
    /// the audio is a transcode here too.
    /// </summary>
    public static string PlayMethod(PlaybackPlan plan) => StreamMethods.Of(plan) is var method and not StreamMethod.None ? StreamMethods.Label(method) : None;

    public static string CodecLabel(string? codec, string fallback = None) =>
        codec?.Trim() is { Length: > 0 } trimmed ? CodecLabels.GetValueOrDefault(trimmed) ?? trimmed.ToUpperInvariant() : fallback;

    /// <summary>"DV", "DV HDR10", "HDR10+", "HDR10", "HLG", "HDR" or empty for SDR (web: videoRangeLabel).</summary>
    public static string VideoRangeLabel(WatchFileVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return VideoRangeLabel(version.VideoTracks, version.Hdr);
    }

    /// <summary>The label of a file with <paramref name="videoTracks"/>; <paramref name="hdr"/> is its plain HDR flag, the last resort.</summary>
    public static string VideoRangeLabel(IReadOnlyList<WatchVideoTrack> videoTracks, bool hdr)
    {
        ArgumentNullException.ThrowIfNull(videoTracks);
        bool dolbyVision = false;
        string type = "";
        foreach (var track in videoTracks)
        {
            dolbyVision |= !string.IsNullOrWhiteSpace(track.DolbyVision) || track.DvProfile > 0
                || (track.VideoRangeType?.Trim() ?? "").StartsWith("DOVI", StringComparison.Ordinal);
            if (type.Length == 0)
            {
                type = HdrType(track);
            }
        }
        return dolbyVision ? (type.Length > 0 ? $"DV {type}" : "DV") : type.Length > 0 ? type : hdr ? "HDR" : "";
    }

    /// <summary>"4.0 GiB", as the web's file sizes in binary units.</summary>
    public static string FileSize(long bytes) => bytes switch
    {
        <= 0 => None,
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0} GiB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.0} MiB"),
        >= 1L << 10 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} KiB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
    };

    /// <summary>"12.0 Mbps" from kilobits per second.</summary>
    public static string Mbps(long kbps) => kbps > 0 ? string.Create(CultureInfo.InvariantCulture, $"{kbps / 1000.0:0.0} Mbps") : None;

    private static PlaybackInfoSection SourceFile(WatchFileVersion? version, int? audioOrdinal)
    {
        var video = version?.VideoTracks.FirstOrDefault();
        var audio = version is null ? null
            : audioOrdinal is >= 0 and var ordinal && ordinal < version.AudioTracks.Count ? version.AudioTracks[ordinal]
            : version.AudioTracks.FirstOrDefault(t => t.Default) ?? version.AudioTracks.FirstOrDefault();
        return new(AppText.Get("web.player.info.section.source"),
        [
            new(AppText.Get("web.player.info.container"), version?.Container is { Length: > 0 } container && container.Trim().Length > 0 ? container : None),
            new(AppText.Get("web.player.info.size"), FileSize(version?.FileSize ?? 0)),
            new(AppText.Get("web.player.info.bitrate"), Mbps(version?.Bitrate ?? 0)),
            new(AppText.Get("web.player.info.videoCodec"), CodecLabel(video?.Codec ?? version?.CodecVideo) is var codec && codec != None ? Join(" ", codec, video?.Profile?.Trim()) : None),
            new(AppText.Get("web.player.info.videoBitrate"), Mbps(video?.Bitrate ?? 0)),
            new(AppText.Get("web.player.info.videoRangeType"), VideoRangeType(version, video)),
            new(AppText.Get("web.player.info.colorRange"), (video?.ColorRange?.Trim().ToLowerInvariant()) switch
            {
                "tv" => AppText.Get("web.player.info.colorRangeLimited"),
                "pc" => AppText.Get("web.player.info.colorRangeFull"),
                "unknown" => AppText.Get("web.player.info.colorRangeUnknown"),
                _ => None,
            }),
            new(AppText.Get("web.player.info.audioCodec"), audio is null ? CodecLabel(version?.CodecAudio) : AudioCodec(version!, audio)),
            new(AppText.Get("web.player.info.audioBitrate"), audio?.Bitrate > 0 ? string.Create(CultureInfo.InvariantCulture, $"{audio.Bitrate:N0} kbps") : None),
            new(AppText.Get("web.player.info.audioChannels"), audio?.Channels > 0 ? audio.Channels.ToString(CultureInfo.InvariantCulture) : None),
            new(AppText.Get("web.player.info.audioSampleRate"), audio?.SampleRate > 0 ? string.Create(CultureInfo.InvariantCulture, $"{audio.SampleRate:N0} Hz") : None),
        ]);
    }

    // What is on the wire and how it got there: the plan says whether the server re-encoded, nothing is guessed. Where a
    // transformation Granary does not know may have touched the stream (encoded is null), the line claims neither.
    private static string Delivered(PlaybackPlan plan, string? codec, bool? encoded)
    {
        string label = CodecLabel(codec);
        if (label == None)
        {
            return None;
        }
        if (plan.Delivery == PlaybackDeliveries.OriginalHttp)
        {
            return AppText.Get("web.player.info.codec.direct", ("codec", label));
        }
        return encoded switch
        {
            true => AppText.Get("web.player.info.codec.transcoded", ("codec", label)),
            false => AppText.Get("web.player.info.codec.copy", ("codec", label)),
            null => label,
        };
    }

    private static string VideoRangeType(WatchFileVersion? version, WatchVideoTrack? track)
    {
        if (track?.DolbyVision?.Trim() is { Length: > 0 } dolbyVision)
        {
            string label = dolbyVision.StartsWith("dolby vision", StringComparison.OrdinalIgnoreCase) ? dolbyVision : $"Dolby Vision {dolbyVision}";
            return track.VideoRange?.Trim() is { Length: > 0 } range ? $"{label} ({range})" : label;
        }
        if (track?.VideoRange?.Trim() is { Length: > 0 } videoRange)
        {
            return videoRange;
        }
        return version is null ? None : VideoRangeLabel(version) is { Length: > 0 } rangeLabel ? rangeLabel : "SDR";
    }

    // An Atmos track whose title does not say so shows its family; otherwise its title, else the codec. The title says so
    // with the word: "Atmospheric score" does not (web: formatOriginalAudioCodec with mentionsAtmos).
    private static string AudioCodec(WatchFileVersion version, WatchAudioTrack track)
    {
        string? title = track.Title?.Trim() is { Length: > 0 } own ? own : track.EmbeddedTitle?.Trim() is { Length: > 0 } embedded ? embedded : null;
        string family = TrackText.AudioCodec(track);
        if (family.Contains("Atmos", StringComparison.Ordinal) && !TrackText.MentionsAtmos(title))
        {
            return family;
        }
        return title ?? CodecLabel(track.Codec ?? version.CodecAudio);
    }

    // web/src/lib/videoRange.ts: trackHdrType.
    private static string HdrType(WatchVideoTrack track)
    {
        string rangeType = track.VideoRangeType?.Trim() ?? "";
        if (track.Hdr10Plus || rangeType.Contains("HDR10Plus", StringComparison.Ordinal))
        {
            return "HDR10+";
        }
        if (rangeType == "HDR10" || rangeType.EndsWith("WithHDR10", StringComparison.Ordinal))
        {
            return "HDR10";
        }
        if (rangeType == "HLG" || rangeType.EndsWith("WithHLG", StringComparison.Ordinal))
        {
            return "HLG";
        }
        string transfer = track.ColorTransfer?.ToLowerInvariant() ?? "";
        return transfer.Contains("smpte2084", StringComparison.Ordinal) ? "HDR10"
            : transfer.Contains("arib-std-b67", StringComparison.Ordinal) ? "HLG"
            : "";
    }

    private static string Dimensions(int width, int height) =>
        width > 0 && height > 0 ? string.Create(CultureInfo.InvariantCulture, $"{width}x{height}") : None;

    private static string FrameCount(long count) => count >= 0 ? count.ToString(CultureInfo.InvariantCulture) : None;

    private static string Join(string separator, params string?[] parts) =>
        string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
