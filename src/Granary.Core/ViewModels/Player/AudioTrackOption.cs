using Granary.Core.Api.Playback;

namespace Granary.Core.ViewModels.Player;

/// <summary>
/// One row of the player's audio menu, as Silo's web player draws it (AudioTrackMenu): the track's name with chips for
/// codec, channels and the file's default, and a line of details under it.
/// </summary>
/// <param name="Ordinal">The track's place in its file's audio tracks; what a pick passes on.</param>
public sealed record AudioTrackOption(int Ordinal, string Title, string Meta, string Codec, string Channels, bool IsDefault, bool IsSelected)
{
    /// <summary>For screen readers: the row as one sentence, the chips in their own words.</summary>
    public string AccessibleName => string.Join(", ", new[] { Title, $"{Codec} {Channels}".Trim(), IsDefault ? "default" : "", Meta }
        .Where(part => part.Length > 0));

    /// <summary>The menu for <paramref name="tracks"/>, with the row of <paramref name="selected"/> checked.</summary>
    public static IReadOnlyList<AudioTrackOption> For(IReadOnlyList<WatchAudioTrack> tracks, int? selected, IFormatProvider? culture = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return [.. tracks.Select((track, ordinal) =>
        {
            string title = TrackText.AudioTitle(track);
            return new AudioTrackOption(ordinal, title, TrackText.AudioMeta(track, title, culture), TrackText.AudioCodec(track),
                TrackText.Channels(track.Channels), track.Default, ordinal == selected);
        })];
    }
}
