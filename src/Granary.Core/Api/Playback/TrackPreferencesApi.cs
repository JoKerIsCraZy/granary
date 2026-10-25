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

/// <summary>
/// How the server and every client recognize a chosen subtitle track in the series' other files: the same members as
/// the server's SubtitleTrackSignature, which the web, Apple and Android clients send.
/// </summary>
public sealed record SubtitleTrackSignature
{
    /// <summary><see cref="SubtitleSources"/>.</summary>
    public string? Source { get; init; }

    public string? Language { get; init; }

    public string? Codec { get; init; }

    public string? Label { get; init; }

    public bool Forced { get; init; }

    public bool HearingImpaired { get; init; }
}

/// <summary>
/// The body of <c>PUT /api/v2/subtitle-prefs/{key}</c> (docs/api-contract.md, 8.8): the viewer's subtitle pick, or
/// subtitles off. <c>show_forced_subtitles</c> is left out on purpose, as Android leaves it out: sending it would pin
/// the profile's forced-subtitle setting on this series, so a later change to that setting would never reach it.
/// </summary>
public sealed record SubtitleChoice
{
    /// <summary>The track's language as the server takes it; empty for off or a track without one.</summary>
    public string SubtitleLanguage { get; init => field = value ?? ""; } = "";

    /// <summary>The inventory's combined ordinal; -1 for off.</summary>
    public int SubtitleTrackIndex { get; init; }

    /// <summary><c>always</c> for a track, <c>off</c> for none.</summary>
    public string SubtitleMode { get; init => field = value ?? ""; } = "";

    public SubtitleTrackSignature? TrackSignature { get; init; }

    /// <summary>Subtitles off for the series, as the web and Apple spell it.</summary>
    public static SubtitleChoice Off { get; } = new() { SubtitleTrackIndex = -1, SubtitleMode = SubtitlePreferenceModes.Off };

    /// <summary>The pick of an inventory entry; <paramref name="hearingImpaired"/> includes what its label says.</summary>
    public static SubtitleChoice Of(SubtitleInventoryItem track, bool hearingImpaired)
    {
        ArgumentNullException.ThrowIfNull(track);
        return new SubtitleChoice
        {
            SubtitleLanguage = AudioPreference.LanguageTag(track.Language) ?? "",
            SubtitleTrackIndex = Math.Max(-1, track.CombinedIndex),
            SubtitleMode = SubtitlePreferenceModes.Always,
            TrackSignature = new SubtitleTrackSignature
            {
                Source = Clean(track.Source),
                Language = Clean(track.Language),
                Codec = Clean(track.Codec),
                Label = Clean(track.Label),
                Forced = track.Forced,
                HearingImpaired = hearingImpaired,
            },
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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

    /// <summary>Saves the viewer's subtitle pick, or off, under <paramref name="key"/> (see <see cref="KeyFor"/>); the server answers 204.</summary>
    public Task SaveSubtitleAsync(Uri server, string key, SubtitleChoice choice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choice);
        return http.SendAsync(HttpMethod.Put, server, "api/v2/subtitle-prefs/" + SiloHttp.PathSegment(key, nameof(key)), SiloAuth.Profile,
            choice, PlaybackJsonContext.Default.SubtitleChoice, cancellationToken);
    }

    /// <summary>Where a title's picks are kept: under its series for an episode, else under the title itself.</summary>
    public static string KeyFor(WatchDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return detail.Type == WatchItemTypes.Episode && !string.IsNullOrWhiteSpace(detail.SeriesId) ? detail.SeriesId : detail.ContentId;
    }
}
