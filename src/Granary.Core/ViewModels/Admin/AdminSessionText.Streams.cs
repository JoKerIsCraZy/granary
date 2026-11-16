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

public static partial class AdminSessionText
{
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
