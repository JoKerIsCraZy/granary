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
    private const string ServerExecutor = "server";

    // The server names an encode after its target (video_to_h264, video_to_hevc, audio_to_aac); its tone mapping encodes
    // the video too.
    private const string VideoEncode = "video_to_";
    private const string AudioEncode = "audio_to_";
    private const string ToneMap = "hdr_to_sdr_tonemap";

    // The server's transformations that only rewrite Dolby Vision metadata and copy the picture (the API's section 11).
    // Any other one, also one this app does not know yet, may encode, so it counts as a transcode.
    private static readonly HashSet<string> MetadataOnly = new(StringComparer.Ordinal) { "server_dv7_to_hdr10", "server_dv7_to_dv81" };

    /// <summary>
    /// The method of <paramref name="plan"/>, from what the server does to it: any transformation of its own but a Dolby
    /// Vision strip makes a transcode, also on a route that copies the video and converts only the audio.
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
        if (plan.Delivery == PlaybackDeliveries.ServerTranscodeHls
            || plan.Transformations.Any(t => t.Executor == ServerExecutor && !MetadataOnly.Contains(t.Name)))
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

    /// <summary>The server encodes the video of <paramref name="plan"/>: to another codec, or tone mapped to SDR.</summary>
    internal static bool EncodesVideo(PlaybackPlan plan) =>
        ServerDoes(plan, name => name.StartsWith(VideoEncode, StringComparison.Ordinal) || name == ToneMap);

    /// <summary>The server encodes the audio of <paramref name="plan"/>.</summary>
    internal static bool EncodesAudio(PlaybackPlan plan) => ServerDoes(plan, name => name.StartsWith(AudioEncode, StringComparison.Ordinal));

    private static bool ServerDoes(PlaybackPlan plan, Func<string, bool> transformation) =>
        plan.Transformations.Any(t => t.Executor == ServerExecutor && transformation(t.Name));
}
