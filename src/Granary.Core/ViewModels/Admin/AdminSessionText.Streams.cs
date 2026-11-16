using Granary.Core.Api.Admin;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Admin;

/// <summary>One stream's source format and, when the server converts it, what it delivers: the web's StreamConversion.</summary>
/// <param name="Source">The source, compact: "HEVC 2160p", "TrueHD 7.1".</param>
/// <param name="Delivered">What the viewer gets instead; null when the stream reaches them untouched.</param>
public sealed record AdminStreamConversion(string Source, string? Delivered)
{
    public bool IsConverted => Delivered is not null;

    /// <summary>"HEVC 2160p → H.264 1080p" for a converted stream, the source alone for an untouched one.</summary>
    public string Text => Delivered is null
        ? Source
        : AppText.Get("web.admin.activity.detail.conversion", ("source", Source), ("output", Delivered));
}

/// <summary>A stream's bitrate as the web's BitrateCell shows it (<c>getSessionBitrate</c>).</summary>
/// <param name="Live">The rate the viewer receives, "8.0 Mbps"; empty when unknown.</param>
/// <param name="Source">The source file's bitrate; empty when unknown.</param>
/// <param name="Limit">The ceiling a transcode encodes to; empty without one.</param>
/// <param name="Share">The delivered rate as a share of the source's, 0 to 1; null unless both are known.</param>
public sealed record AdminSessionBitrate(string Live, string Source, string Limit, double? Share)
{
    /// <summary>A transcode squeezes the stream to a limit: the bar shows in the warning colour, else in success.</summary>
    public bool IsLimited => Limit.Length > 0;

    /// <summary>What the rate is measured against: "limit 4.0 Mbps · source 20.0 Mbps", either alone, or empty.</summary>
    public string Reference => (Limit.Length > 0, Source.Length > 0) switch
    {
        (true, true) => AppText.Get("web.admin.activity.bitrate.limitAndSource", ("limit", Limit), ("source", Source)),
        (true, false) => AppText.Get("web.admin.activity.bitrate.limit", ("rate", Limit)),
        (false, true) => AppText.Get("web.admin.activity.bitrate.source", ("rate", Source)),
        _ => "",
    };

    /// <summary>"limit 4.0 Mbps", the tag the details' Bitrate carries; empty without a limit.</summary>
    public string LimitTag => IsLimited ? AppText.Get("web.admin.activity.bitrate.limit", ("rate", Limit)) : "";
}

public static partial class AdminSessionText
{
    /// <summary>
    /// The rate the viewer receives, the source's, and a transcode's limit, as the web's <c>getSessionBitrate</c>. The share
    /// is the delivered part of the source: full for an untouched file, short when the route squeezes it.
    /// </summary>
    public static AdminSessionBitrate SessionBitrate(AdminPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        int live = session.StreamBitrateKbps ?? 0;
        int source = session.SourceBitrateKbps ?? 0;
        double? share = live > 0 && source > 0 ? Math.Min(1, (double)live / source) : null;
        return new AdminSessionBitrate(Bitrate(live), Bitrate(source), Bitrate(session.TargetBitrateKbps), share);
    }

    /// <summary>
    /// The video's source and, for a video transcode, its output, as the web's <c>getVideoConversion</c>: a copy or direct
    /// play leaves the video untouched.
    /// </summary>
    public static AdminStreamConversion VideoConversion(AdminPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string source = Compact(Codec(session.SourceVideoCodec), session.SourceVideoResolution?.Trim()) is { Length: > 0 } format ? format : UnknownSource;
        if (!IsVideoTranscode(session))
        {
            return new AdminStreamConversion(source, null);
        }
        string delivered = Compact(Codec(session.TargetVideoCodec), DeliveredResolution(session)) is { Length: > 0 } target
            ? target
            : AppText.Get("web.admin.activity.detail.transcoding");
        return new AdminStreamConversion(source, delivered);
    }

    /// <summary>
    /// The audio's source and, when it is re-encoded, its output, as the web's <c>getAudioConversion</c>: codec and layout,
    /// the target's layout only when the server says it (a downmix changed the source's).
    /// </summary>
    public static AdminStreamConversion AudioConversion(AdminPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string source = Compact(Codec(session.SourceAudioCodec), Layout(session.SourceAudioChannels)) is { Length: > 0 } format ? format : UnknownSource;
        if (Or(session.AudioDecision, session.TranscodeAudio ? "transcode" : session.PlayMethod) != "transcode")
        {
            return new AdminStreamConversion(source, null);
        }
        string delivered = Compact(Codec(Or(session.TargetAudioCodec, "aac")), Layout(session.TargetAudioChannels)) is { Length: > 0 } target
            ? target
            : AppText.Get("web.admin.activity.transcodeMode.audioTranscode");
        return new AdminStreamConversion(source, delivered);
    }

    /// <summary>The encoder of a video transcode, "HW QSV" or "SW", which the web's video line ends with; empty otherwise.</summary>
    public static string VideoEncoder(AdminPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return IsVideoTranscode(session) ? TranscodeMode(session) : "";
    }

    private static bool IsVideoTranscode(AdminPlaybackSession session) => Or(session.VideoDecision, session.PlayMethod) == "transcode";

    // The web's compactFormat: the known parts, a space apart.
    private static string Compact(params string?[] parts) => Join(" ", parts);
}
