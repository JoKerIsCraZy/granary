using System.Globalization;
using Granary.Core.Api.Admin;

namespace Granary.Core.ViewModels.Admin;

/// <summary>A node a session's stream passes: "Transcode gpu", "Proxy edge", "Server Local server".</summary>
/// <param name="Label">Transcode, Proxy, Server, or Node for an older server's report.</param>
public sealed record AdminRouteNode(string Key, string Label, string Name);

/// <summary>
/// How Activity words a session's stream, as the web's <c>adminActivityPresentation.ts</c>: what reaches the viewer
/// (container, video, audio), its bitrate, where it is and how long it has played, and the nodes it passes.
/// </summary>
public static class AdminSessionText
{
    private static readonly Dictionary<string, string> CodecLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["aac"] = "AAC", ["ac3"] = "AC3", ["av1"] = "AV1", ["dts"] = "DTS", ["dtshd"] = "DTS-HD", ["eac3"] = "EAC3", ["flac"] = "FLAC",
        ["h264"] = "H.264", ["hevc"] = "HEVC", ["mp3"] = "MP3", ["opus"] = "Opus", ["truehd"] = "TrueHD",
    };

    /// <summary>"S1E3 · Dark" for an episode, "Movie" or "Series" otherwise; empty when the server says nothing.</summary>
    public static string Subtitle(AdminPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!string.IsNullOrEmpty(session.SeriesName) && session.SeasonNumber is { } season && session.EpisodeNumber is { } episode)
        {
            return $"S{season}E{episode} · {session.SeriesName}";
        }
        return session.MediaType switch
        {
            "movie" => "Movie",
            "series" => "Series",
            _ => "",
        };
    }

    /// <summary>The container that reaches the viewer: the source's for direct play, else the output's ("fMP4 (HLS)").</summary>
    public static string Container(AdminPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.PlayMethod.Trim() == "direct")
        {
            return Upper(session.SourceContainer) ?? "Unknown source";
        }
        bool hls = session.OutputProtocol == "hls";
        string? container = session.OutputContainer?.Trim().ToLowerInvariant();
        string label = container switch
        {
            null or "" => "Unknown output container",
            "fmp4" => "fMP4",
            "mpegts" => "MPEG-TS",
            _ => container.ToUpperInvariant(),
        };
        return hls ? $"{label} (HLS)" : label;
    }

    /// <summary>The video that reaches the viewer: the target's codec and size for a transcode, else the source's.</summary>
    public static string Video(AdminPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if ((Or(session.VideoDecision, session.PlayMethod)) != "transcode")
        {
            return Join(" · ", Codec(session.SourceVideoCodec), session.SourceVideoResolution?.Trim()) is { Length: > 0 } source ? source : "Unknown source";
        }
        return Join(" · ", Codec(session.TargetVideoCodec), DeliveredResolution(session)) is { Length: > 0 } target ? target : "Transcoding";
    }

    /// <summary>
    /// The audio that reaches the viewer: the target's codec and layout for a transcode (never the source's layout, which a
    /// downmix changed), else the source track's name, codec and layout.
    /// </summary>
    public static string Audio(AdminPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string decision = Or(session.AudioDecision, session.TranscodeAudio ? "transcode" : session.PlayMethod);
        if (decision == "transcode")
        {
            return Join(" ", Codec(Or(session.TargetAudioCodec, "aac")), Layout(session.TargetAudioChannels)) is { Length: > 0 } target ? target : "Audio Transcode";
        }
        string? lead = session.SourceAudioTitle?.Trim() is { Length: > 0 } title ? title : session.SourceAudioLanguage?.Trim();
        string format = Join(" ", Codec(session.SourceAudioCodec), Layout(session.SourceAudioChannels));
        return Join(" · ", lead, format) is { Length: > 0 } source ? source : "Unknown source";
    }

    /// <summary>"8.0 Mbps", "640 kbps"; empty when unknown.</summary>
    public static string Bitrate(int? kbps) => kbps switch
    {
        null or <= 0 => "",
        >= 1000 => $"{(kbps.Value / 1000d).ToString("0.0", CultureInfo.InvariantCulture)} Mbps",
        _ => $"{kbps.Value} kbps",
    };

    /// <summary>"Audio Transcode", "HW QSV", "SW", "HW/SW unknown" while something is re-encoded; empty otherwise.</summary>
    public static string TranscodeMode(AdminPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string video = Or(session.VideoDecision, session.PlayMethod);
        string audio = Or(session.AudioDecision, session.TranscodeAudio ? "transcode" : session.PlayMethod);
        if (video != "transcode")
        {
            return audio == "transcode" ? "Audio Transcode" : "";
        }
        return session.TranscodeHwAccel?.Trim().ToLowerInvariant() switch
        {
            null or "" => "HW/SW unknown",
            "qsv" => "HW QSV",
            "vaapi" => "HW VAAPI",
            "videotoolbox" => "HW VideoToolbox",
            "none" => "SW",
            "auto" => "HW/SW pending",
            var accel => $"HW {accel.ToUpperInvariant()}",
        };
    }

    /// <summary>"12:04", "1:02:09".</summary>
    public static string Clock(double seconds)
    {
        long total = double.IsFinite(seconds) ? Math.Max(0, (long)Math.Floor(seconds)) : 0;
        long hours = total / 3600;
        long minutes = total % 3600 / 60;
        long rest = total % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{rest:00}");
    }

    /// <summary>"12:04 / 48:30", or the position alone when the file's length is unknown.</summary>
    public static string Position(AdminPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.FileDuration is > 0 and var duration
            ? $"{Clock(session.PositionSeconds)} / {Clock(duration)}"
            : Clock(session.PositionSeconds);
    }

    /// <summary>How far into the file, 0 to 1; 0 when its length is unknown.</summary>
    public static double Progress(AdminPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.FileDuration is > 0 and var duration ? Math.Clamp(session.PositionSeconds / duration, 0, 1) : 0;
    }

    /// <summary>
    /// The nodes the stream passes, from where the work runs to where it leaves for the viewer. A session the server did
    /// not route names its reporting node, as older servers report.
    /// </summary>
    public static IReadOnlyList<AdminRouteNode> Route(AdminPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string execution = session.RoutingExecution?.Trim() ?? "";
        string egress = session.RoutingEgress?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(session.RoutingWorkload) && execution.Length == 0 && egress.Length == 0)
        {
            string reported = Or(session.NodeDisplayName, session.ReportingNode);
            string name = reported.Length == 0 || reported.Equals("local", StringComparison.OrdinalIgnoreCase) ? "Local server" : reported;
            return [new AdminRouteNode($"legacy:{name}", "Node", name)];
        }
        var nodes = new List<AdminRouteNode>();
        string executionName = Or(session.RoutingExecutionNodeName, session.NodeDisplayName ?? "");
        if (execution is "transcode" or "proxy")
        {
            nodes.Add(Named(execution, executionName));
        }
        if (egress == "proxy" && Named("proxy", session.RoutingEgressNodeName ?? "") is var proxy && nodes.All(n => n.Key != proxy.Key))
        {
            nodes.Add(proxy);
        }
        if (egress == "api" || (execution == "api" && nodes.Count == 0))
        {
            nodes.Add(ReportingServer(session));
        }
        return nodes.Count > 0 ? nodes : [ReportingServer(session)];
    }

    private static AdminRouteNode Named(string kind, string name)
    {
        string label = kind == "transcode" ? "Transcode" : "Proxy";
        string shown = name.Trim() is { Length: > 0 } trimmed ? trimmed : $"Unknown {kind} node";
        return new AdminRouteNode($"{kind}:{shown}", label, shown);
    }

    private static AdminRouteNode ReportingServer(AdminPlaybackSession session)
    {
        string reported = session.ReportingNode.Trim();
        return reported.Length == 0 || reported.Equals("local", StringComparison.OrdinalIgnoreCase)
            ? new AdminRouteNode("server:local", "Server", "Local server")
            : new AdminRouteNode($"server:{reported}", "Server", reported);
    }

    // The catalog names a file by its size class (3840x1600 is "2160p"), a transcode by its output height ("1600p"): a
    // transcode that keeps the source's height is named as the source, so it does not read as a downscale.
    private static string? DeliveredResolution(AdminPlaybackSession session)
    {
        string? target = session.TargetResolution?.Trim();
        string? source = session.SourceVideoResolution?.Trim();
        if (!string.IsNullOrEmpty(source) && session.SourceVideoHeight is { } height && target is { Length: > 1 } && target.EndsWith('p')
            && int.TryParse(target[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int targetHeight) && targetHeight == height)
        {
            return source;
        }
        return target;
    }

    private static string? Codec(string? codec) =>
        codec?.Trim() is { Length: > 0 } trimmed ? CodecLabels.GetValueOrDefault(trimmed, trimmed.ToUpperInvariant()) : null;

    private static string? Layout(int? channels) => channels switch
    {
        null or <= 0 => null,
        1 => "1.0",
        2 => "2.0",
        6 => "5.1",
        8 => "7.1",
        _ => $"{channels}ch",
    };

    private static string? Upper(string? value) => value?.Trim() is { Length: > 0 } trimmed ? trimmed.ToUpperInvariant() : null;

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Join(string separator, params string?[] parts) => string.Join(separator, parts.Where(part => !string.IsNullOrEmpty(part)));
}
