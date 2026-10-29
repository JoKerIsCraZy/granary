namespace Granary.Core.Abstractions;

/// <summary>
/// Windows' media controls (System Media Transport Controls): the media keys, and the overlay that shows what plays with
/// its picture, buttons and timeline. The player shows its title there and takes the buttons as its own.
/// </summary>
public interface ISystemMediaControls
{
    /// <summary>A button or the timeline was used; raised on any thread.</summary>
    event EventHandler<SystemMediaRequest>? Requested;

    /// <summary>Shows <paramref name="state"/> as a whole; a part equal to what shows already is left alone.</summary>
    void Show(SystemMediaState state);

    /// <summary>Takes the app off the controls: nothing plays any more.</summary>
    void Clear();
}

/// <summary>What the controls say about playback.</summary>
public enum SystemMediaStatus
{
    /// <summary>A title loads.</summary>
    Changing,

    Playing,

    Paused,

    /// <summary>The title ended or failed.</summary>
    Stopped,
}

/// <summary>What the viewer used on the controls.</summary>
public enum SystemMediaButton
{
    Play,
    Pause,
    Previous,
    Next,

    /// <summary>The controls' rewind: the player's back skip.</summary>
    Rewind,

    /// <summary>The controls' fast forward: the player's forward skip.</summary>
    FastForward,

    /// <summary>A place on the timeline, in <see cref="SystemMediaRequest.PositionSeconds"/>.</summary>
    Seek,
}

/// <summary>A button of the controls, or a place on their timeline for <see cref="SystemMediaButton.Seek"/>.</summary>
public sealed record SystemMediaRequest(SystemMediaButton Button, double PositionSeconds = 0);

/// <summary>
/// Everything the controls show: the title's two lines and picture, the playback status, the timeline in the title's own
/// time, and which buttons work.
/// </summary>
/// <param name="Artwork">The picture's address, for the app to load; it never goes to Windows (it is presigned).</param>
public sealed record SystemMediaState(
    string Title,
    string Subtitle,
    Uri? Artwork,
    SystemMediaStatus Status,
    double PositionSeconds,
    double DurationSeconds,
    bool CanPlayPause,
    bool CanSeek,
    bool CanGoPrevious,
    bool CanGoNext);
