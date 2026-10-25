using System.Text.RegularExpressions;

namespace Granary.Core.Api.Playback;

/// <summary>
/// How the server recognizes a chosen audio track in the series' other files: the catalog's own fields for it,
/// matched exactly. <c>default</c> is left out, it says nothing about the track the viewer wants.
/// </summary>
public sealed record AudioTrackSignature
{
    public string? Language { get; init; }

    public string? Title { get; init; }

    public string? EmbeddedTitle { get; init; }

    public string? Codec { get; init; }

    public string? Layout { get; init; }

    public int? Channels { get; init; }

    /// <summary>The signature of <paramref name="track"/>; null when the track has nothing to match by.</summary>
    public static AudioTrackSignature? Of(WatchAudioTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var signature = new AudioTrackSignature
        {
            Language = Clean(track.Language),
            Title = Clean(track.Title),
            EmbeddedTitle = Clean(track.EmbeddedTitle),
            Codec = Clean(track.Codec),
            Layout = Clean(track.Layout),
            Channels = track.Channels > 0 ? track.Channels : null,
        };
        return signature == new AudioTrackSignature() ? null : signature;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The body of <c>PUT /api/v2/audio-prefs/{key}</c> (docs/api-contract.md, 8.8): the viewer's audio pick, which the
/// server applies to every episode of the series and folds into the next watch detail.
/// </summary>
public sealed partial record AudioPreference
{
    /// <summary>The track's ordinal in its file's <c>audio_tracks</c>.</summary>
    public int AudioTrackIndex { get; init; }

    /// <summary>Also stored as the series' audio language; left out for a track without a spoken language.</summary>
    public string? AudioLanguage { get; init; }

    public AudioTrackSignature? TrackSignature { get; init; }

    /// <summary>The pick of <paramref name="track"/>, at <paramref name="ordinal"/> of its file's audio tracks.</summary>
    public static AudioPreference Of(WatchAudioTrack track, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        return new AudioPreference
        {
            AudioTrackIndex = ordinal,
            AudioLanguage = LanguageTag(track.Language),
            TrackSignature = AudioTrackSignature.Of(track),
        };
    }

    // A tag the server accepts (anything else is 422), and none for codes that name no spoken language: saving one
    // would invent a language preference the viewer never expressed (Silo's web player and Android leave them out).
    internal static string? LanguageTag(string? language)
    {
        string tag = language?.Trim() ?? "";
        if (!LanguageTagPattern().IsMatch(tag))
        {
            return null;
        }
        string primary = tag.Split('-', '_')[0].ToLowerInvariant();
        return primary is "und" or "mul" or "zxx" or "mis" ? null : tag;
    }

    [GeneratedRegex("^[A-Za-z]{2,3}(?:[-_][0-9A-Za-z]{1,8})*$")]
    private static partial Regex LanguageTagPattern();
}

/// <summary>The profile's track choices kept on the server (docs/api-contract.md, 8.8). They need <c>X-Profile-Id</c>.</summary>
public sealed class TrackPreferencesApi(SiloHttp http)
{
    /// <summary>Saves the viewer's audio pick under <paramref name="key"/> (see <see cref="KeyFor"/>); the server answers 204.</summary>
    public Task SaveAudioAsync(Uri server, string key, AudioPreference preference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preference);
        return http.SendAsync(HttpMethod.Put, server, "api/v2/audio-prefs/" + SiloHttp.PathSegment(key, nameof(key)), SiloAuth.Profile,
            preference, PlaybackJsonContext.Default.AudioPreference, cancellationToken);
    }

    /// <summary>Where a title's picks are kept: under its series for an episode, else under the title itself.</summary>
    public static string KeyFor(WatchDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return detail.Type == WatchItemTypes.Episode && !string.IsNullOrWhiteSpace(detail.SeriesId) ? detail.SeriesId : detail.ContentId;
    }
}
