using System.Globalization;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;

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
        List<PlaybackInfoRow> player = [new("Player", "mpv")];
        if (stream is not null)
        {
            player.Add(new("Play method", Delivery(stream.Plan.Delivery)));
            player.Add(new("Protocol", stream.Url.Scheme));
            player.Add(new("Stream type", stream.Plan.Stream.Protocol == StreamProtocols.Hls ? "HLS" : "Progressive"));
            if (stream.Requested is { } requested && stream.Playing is { } playing && requested.FileId != playing.FileId)
            {
                player.Add(new("Auto-switched from", Join(" ", requested.Resolution.Trim(), CodecLabel(requested.CodecVideo, ""), VideoRangeLabel(requested))));
            }
        }
        List<PlaybackInfoSection> sections =
        [
            new("Player", player),
            new("Video Info",
            [
                new("Player dimensions", Dimensions(playerWidth, playerHeight)),
                new("Video resolution", Dimensions(stats.VideoWidth, stats.VideoHeight)),
                new("Decoding", stats.HardwareDecoder is { Length: > 0 } hwdec and not "no" ? $"Hardware ({hwdec})" : stats.VideoWidth > 0 ? "Software" : None),
                new("Dropped frames", FrameCount(stats.DroppedFrames)),
                new("Decoder-dropped frames", FrameCount(stats.DecoderDroppedFrames)),
            ]),
        ];
        if (stream is null)
        {
            return sections;
        }
        sections.Add(new("Playback Stream Info",
        [
            new("Video codec", Delivered(stream.Plan, stream.Plan.EffectiveRecipe.VideoCodec, "video_to_h264")),
            new("Audio codec", Delivered(stream.Plan, stream.Plan.EffectiveRecipe.AudioCodec, "audio_to_aac")),
        ]));
        sections.Add(SourceFile(stream.Playing, stream.AudioOrdinal));
        return sections;
    }

    /// <summary>The route the server chose, from the plan's delivery alone (web: formatDelivery).</summary>
    public static string Delivery(string delivery) => delivery switch
    {
        PlaybackDeliveries.OriginalHttp => "Direct Play",
        PlaybackDeliveries.ServerRemuxProgressive or PlaybackDeliveries.ServerRemuxHls => "Direct Streaming",
        PlaybackDeliveries.ServerTranscodeHls => "Transcode",
        _ => None,
    };

    public static string CodecLabel(string? codec, string fallback = None) =>
        codec?.Trim() is { Length: > 0 } trimmed ? CodecLabels.GetValueOrDefault(trimmed) ?? trimmed.ToUpperInvariant() : fallback;

    /// <summary>"DV", "DV HDR10", "HDR10+", "HDR10", "HLG", "HDR" or empty for SDR (web: videoRangeLabel).</summary>
    public static string VideoRangeLabel(WatchFileVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        bool dolbyVision = false;
        string hdr = "";
        foreach (var track in version.VideoTracks)
        {
            dolbyVision |= !string.IsNullOrWhiteSpace(track.DolbyVision) || track.DvProfile > 0
                || (track.VideoRangeType?.Trim() ?? "").StartsWith("DOVI", StringComparison.Ordinal);
            if (hdr.Length == 0)
            {
                hdr = HdrType(track);
            }
        }
        return dolbyVision ? (hdr.Length > 0 ? $"DV {hdr}" : "DV") : hdr.Length > 0 ? hdr : version.Hdr ? "HDR" : "";
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
        return new("Current Source File",
        [
            new("Container", version?.Container is { Length: > 0 } container && container.Trim().Length > 0 ? container : None),
            new("Size", FileSize(version?.FileSize ?? 0)),
            new("Bitrate", Mbps(version?.Bitrate ?? 0)),
            new("Video codec", CodecLabel(video?.Codec ?? version?.CodecVideo) is var codec && codec != None ? Join(" ", codec, video?.Profile?.Trim()) : None),
            new("Video bitrate", Mbps(video?.Bitrate ?? 0)),
            new("Video range type", VideoRangeType(version, video)),
            new("Color range", (video?.ColorRange?.Trim().ToLowerInvariant()) switch
            {
                "tv" => "Limited (tv)",
                "pc" => "Full (pc)",
                "unknown" => "Unknown",
                _ => None,
            }),
            new("Audio codec", audio is null ? CodecLabel(version?.CodecAudio) : AudioCodec(version!, audio)),
            new("Audio bitrate", audio?.Bitrate > 0 ? string.Create(CultureInfo.InvariantCulture, $"{audio.Bitrate:N0} kbps") : None),
            new("Audio channels", audio?.Channels > 0 ? audio.Channels.ToString(CultureInfo.InvariantCulture) : None),
            new("Audio sample rate", audio?.SampleRate > 0 ? string.Create(CultureInfo.InvariantCulture, $"{audio.SampleRate:N0} Hz") : None),
        ]);
    }

    // What is on the wire and how it got there: the plan says whether the server re-encoded, nothing is guessed.
    private static string Delivered(PlaybackPlan plan, string? codec, string transformation)
    {
        string label = CodecLabel(codec);
        if (label == None)
        {
            return None;
        }
        if (plan.Delivery == PlaybackDeliveries.OriginalHttp)
        {
            return $"{label} (direct)";
        }
        bool transcoded = plan.Transformations.Any(t => t.Executor == "server" && t.Name == transformation);
        return $"{label} ({(transcoded ? "transcoded" : "copy")})";
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

    // An Atmos track whose title does not say so shows its family; otherwise its title, else the codec.
    private static string AudioCodec(WatchFileVersion version, WatchAudioTrack track)
    {
        string? title = track.Title?.Trim() is { Length: > 0 } own ? own : track.EmbeddedTitle?.Trim() is { Length: > 0 } embedded ? embedded : null;
        string family = TrackText.AudioCodec(track);
        if (family.Contains("Atmos", StringComparison.Ordinal) && title?.Contains("atmos", StringComparison.OrdinalIgnoreCase) != true)
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
