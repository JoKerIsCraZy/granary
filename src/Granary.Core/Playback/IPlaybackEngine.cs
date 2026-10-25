namespace Granary.Core.Playback;

/// <summary>What to play: a URL plus the HTTP headers every request for it must carry.</summary>
public sealed record MediaSource(Uri Url, IReadOnlyList<KeyValuePair<string, string>> Headers, TimeSpan? Start = null)
{
    public static MediaSource Local(string path, TimeSpan? start = null) => new(new Uri(Path.GetFullPath(path)), [], start);
}

/// <summary>A consistent view of the player state at one moment.</summary>
/// <param name="HasStarted">Playback began after the last load: the first frame is out (or the first audio for audio only).</param>
/// <param name="IsBuffering">Paused by the player itself until enough data arrived.</param>
/// <param name="Volume">0 to 100; kept across loads.</param>
public sealed record PlaybackSnapshot(
    bool IsLoaded,
    bool IsPaused,
    TimeSpan Position,
    TimeSpan? Duration,
    string? HardwareDecoder,
    bool HasStarted = false,
    bool IsBuffering = false,
    double Volume = 100,
    bool IsMuted = false)
{
    public static PlaybackSnapshot Idle { get; } = new(false, false, TimeSpan.Zero, null, null);
}

public enum PlaybackEndReason
{
    /// <summary>The media really ended.</summary>
    EndOfFile,

    /// <summary>Stopped or replaced on request.</summary>
    Stopped,

    /// <summary>The server rejected the credentials (HTTP 401/403); refresh the token and reload.</summary>
    AuthenticationFailed,

    /// <summary>Any other load or playback failure.</summary>
    Error,
}

public sealed record PlaybackEnded(PlaybackEndReason Reason, string? Detail);

/// <summary>
/// The playback engine as the view models see it. Events are raised on a background thread;
/// consumers marshal to the UI thread themselves.
/// </summary>
public interface IPlaybackEngine : IDisposable
{
    PlaybackSnapshot Current { get; }

    event EventHandler<PlaybackSnapshot>? StateChanged;

    event EventHandler<PlaybackEnded>? Ended;

    void Load(MediaSource source);

    void SetPaused(bool paused);

    void Seek(TimeSpan position);

    /// <summary>Sets the volume in percent; values outside 0 to 100 are clamped.</summary>
    void SetVolume(double percent);

    void SetMuted(bool muted);

    void Stop();
}
