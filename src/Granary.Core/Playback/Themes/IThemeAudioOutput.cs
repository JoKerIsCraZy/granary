using Granary.Core.Api.Catalog;

namespace Granary.Core.Playback.Themes;

/// <summary>
/// The app's light audio player for a title page's theme music, beside mpv: the App implements it on Windows' media player.
/// It plays audio Core already downloaded, so no address or token ever reaches it.
/// </summary>
public interface IThemeAudioOutput
{
    /// <summary>The containers and codecs this PC plays; the server converts a theme in any other to AAC where it can.</summary>
    IReadOnlyList<ThemeAudioFormat> Formats { get; }

    /// <summary>One theme's audio, paused at volume 0 until <see cref="IThemeAudio.Play"/>.</summary>
    /// <param name="contentType">The media type the server named, such as <c>audio/mpeg</c>; null when it named none.</param>
    IThemeAudio Create(ReadOnlyMemory<byte> audio, string? contentType);
}

/// <summary>One theme's audio. Its events may come from any thread; disposing it stops it for good and raises none after.</summary>
public interface IThemeAudio : IDisposable
{
    /// <summary>0 to 1.</summary>
    double Volume { get; set; }

    /// <summary>Plays again from the start at its end, without <see cref="Ended"/>.</summary>
    bool IsLooping { get; set; }

    void Play();

    void Pause();

    /// <summary>The audio moved past its start, once: it can be heard now.</summary>
    event EventHandler? Started;

    /// <summary>It played to its end without looping.</summary>
    event EventHandler? Ended;

    /// <summary>It could not be played, or broke off.</summary>
    event EventHandler? Failed;
}
