namespace Granary.Core.Playback.Themes;

/// <summary>What fetching a theme came to (<see cref="ThemeMusicPlayer"/>, <see cref="ThemeMusic"/>).</summary>
internal sealed class ThemeFetch
{
    /// <summary>
    /// For good: the server refused the grant (403, 404, 406 and the like), named an address Granary does not fetch, or sent
    /// no theme. Asking again would answer the same, so it is not.
    /// </summary>
    public static readonly ThemeFetch Refused = new();

    /// <summary>On the way: the network, a server error (5xx) or a timeout. Tried once more with a new grant.</summary>
    public static readonly ThemeFetch Failed = new();

    private ThemeFetch(ThemeAudioSource? audio = null) => Audio = audio;

    /// <summary>The theme's audio; null when it did not come.</summary>
    public ThemeAudioSource? Audio { get; }

    public static ThemeFetch Of(ThemeAudioSource audio) => new(audio ?? throw new ArgumentNullException(nameof(audio)));
}

/// <summary>A theme Granary does not fetch, or that is no theme: asking again would answer the same.</summary>
public sealed class ThemeRefusedException(string message) : HttpRequestException(HttpRequestError.InvalidResponse, message);
