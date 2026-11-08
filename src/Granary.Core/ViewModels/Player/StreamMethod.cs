using Granary.Core.Api.Playback;

namespace Granary.Core.ViewModels.Player;

/// <summary>
/// How the stream that plays reaches the player: the dot in the settings button's gear. Granary's own; Silo's web player
/// shows no such dot (player/components/QualityMenu.tsx).
/// </summary>
public enum StreamMethod
{
    /// <summary>No server stream: a file played directly, or nothing plays yet.</summary>
    None,

    /// <summary>The original file, byte for byte.</summary>
    DirectPlay,

    /// <summary>The original video and audio in a new container (a remux, copy/copy).</summary>
    DirectStream,

    /// <summary>The server re-encodes the video, the audio or both.</summary>
    Transcode,
}

public static class StreamMethods
{
    // The server names an encode after its target (video_to_h264, video_to_hevc, audio_to_aac).
    internal const string VideoEncode = "video_to_";
    internal const string AudioEncode = "audio_to_";

    /// <summary>
    /// The method of <paramref name="plan"/>, from what the server does to it: an encode of either stream makes a transcode,
    /// also on a route that copies the video. A Dolby Vision strip only rewrites metadata and stays a direct stream.
    /// </summary>
    public static StreamMethod Of(PlaybackPlan? plan)
    {
        if (plan is null)
        {
            return StreamMethod.None;
        }
        if (plan.Delivery == PlaybackDeliveries.OriginalHttp)
        {
            return StreamMethod.DirectPlay;
        }
        if (plan.Delivery == PlaybackDeliveries.ServerTranscodeHls || EncodesOnServer(plan, VideoEncode) || EncodesOnServer(plan, AudioEncode))
        {
            return StreamMethod.Transcode;
        }
        return plan.Delivery is PlaybackDeliveries.ServerRemuxProgressive or PlaybackDeliveries.ServerRemuxHls ? StreamMethod.DirectStream : StreamMethod.None;
    }

    /// <summary>The method in the playback info's words (its "Play method"); empty for <see cref="StreamMethod.None"/>.</summary>
    public static string Label(StreamMethod method) => method switch
    {
        StreamMethod.DirectPlay => "Direct Play",
        StreamMethod.DirectStream => "Direct Streaming",
        StreamMethod.Transcode => "Transcode",
        _ => "",
    };

    /// <summary>The server encodes a stream for <paramref name="plan"/>: one of its transformations starts with <paramref name="prefix"/>.</summary>
    internal static bool EncodesOnServer(PlaybackPlan plan, string prefix) =>
        plan.Transformations.Any(t => t.Executor == "server" && t.Name.StartsWith(prefix, StringComparison.Ordinal));
}
