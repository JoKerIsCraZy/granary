using Granary.Core.Api.Admin;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// How a whole session plays, as the web reduces it (<c>classifyActivityMethod</c>): the server's
/// <c>effective_play_method</c> when it sends one, else its video and audio decisions.
/// </summary>
public static class AdminPlayMethod
{
    public const string Direct = "direct";
    public const string Remux = "remux";

    /// <summary>The video is copied, the audio converted.</summary>
    public const string DirectStream = "direct_stream";

    public const string Transcode = "transcode";
    public const string Unknown = "unknown";

    public static string Of(AdminPlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!string.IsNullOrEmpty(session.EffectivePlayMethod))
        {
            return session.EffectivePlayMethod;
        }
        string video = Decision(Or(session.VideoDecision, session.PlayMethod));
        string audio = Decision(Or(session.AudioDecision, session.TranscodeAudio ? Transcode : session.PlayMethod));
        if (video == Transcode)
        {
            return Transcode;
        }
        // Without a video decision the stream is unknown; a bare audio flag does not make it a direct stream.
        if (audio == Transcode && video.Length > 0)
        {
            return DirectStream;
        }
        if (video == Direct && audio == Direct)
        {
            return Direct;
        }
        return video == "copy" || audio == "copy" ? Remux : Unknown;
    }

    /// <summary>"Direct Play", "Remux", "Direct Stream", "Transcode" or "Unknown", as the web's activityMethodMeta.</summary>
    public static string Label(string method) => method switch
    {
        Direct => AppText.Get("web.common.activity.method.direct.label"),
        Remux => AppText.Get("web.common.activity.method.remux.label"),
        DirectStream => AppText.Get("web.common.activity.method.directStream.label"),
        Transcode => AppText.Get("web.common.activity.method.transcode.label"),
        _ => AppText.Get("web.common.activity.method.unknown.label"),
    };

    /// <summary>The web's order of the methods, by how much work the server does; unknown last, anything else after it.</summary>
    public static int Rank(string method) => method switch
    {
        Direct => 0,
        Remux => 1,
        DirectStream => 2,
        Transcode => 3,
        Unknown => 4,
        _ => 5,
    };

    private static string Or(string? value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;

    private static string Decision(string? decision) => decision?.Trim() switch
    {
        "direct" => Direct,
        "copy" or "remux" => "copy",
        "transcode" => Transcode,
        _ => "",
    };
}
