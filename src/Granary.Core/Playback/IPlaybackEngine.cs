namespace Granary.Core.Playback;

/// <summary>What to play: a URL plus the HTTP headers every request for it must carry.</summary>
/// <param name="AudioStream">The audio stream to play, counted from 1 in the file's order; null leaves it to the player.</param>
/// <param name="SubtitleStream">
/// The subtitle stream to show, counted from 1 in the file's order; 0 shows none, null leaves it to the player.
/// </param>
public sealed record MediaSource(
    Uri Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    TimeSpan? Start = null,
    int? AudioStream = null,
    int? SubtitleStream = null)
{
    public static MediaSource Local(string path, TimeSpan? start = null) => new(new Uri(Path.GetFullPath(path)), [], start);
}

/// <summary>A consistent view of the player state at one moment.</summary>
/// <param name="HasStarted">Playback began after the last load: the first frame is out (or the first audio for audio only).</param>
/// <param name="IsBuffering">Paused by the player itself until enough data arrived.</param>
/// <param name="Volume">0 to 100; kept across loads.</param>
/// <param name="IsAtEnd">
/// The player reached the end of what it could read and holds there, paused. A stream that broke off (a 401 mid-stream
/// among others) ends this way too, so its position then is the end of the data, not necessarily the viewer's.
/// </param>
/// <param name="LoadId">
/// The load this state belongs to, as <see cref="IPlaybackEngine.Load"/> returned it; 0 before the first. Events reach
/// their consumers on another thread, so one raised just before a load can arrive after it.
/// </param>
public sealed record PlaybackSnapshot(
    bool IsLoaded,
    bool IsPaused,
    TimeSpan Position,
    TimeSpan? Duration,
    string? HardwareDecoder,
    bool HasStarted = false,
    bool IsBuffering = false,
    double Volume = 100,
    bool IsMuted = false,
    bool IsAtEnd = false,
    long LoadId = 0)
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

/// <param name="LoadId">The load that ended, as in <see cref="PlaybackSnapshot.LoadId"/>.</param>
public sealed record PlaybackEnded(PlaybackEndReason Reason, string? Detail, long LoadId = 0);

/// <summary>
/// The playback engine as the view models see it. Events are raised on a background thread;
/// consumers marshal to the UI thread themselves.
/// </summary>
public interface IPlaybackEngine : IDisposable
{
    PlaybackSnapshot Current { get; }

    event EventHandler<PlaybackSnapshot>? StateChanged;

    event EventHandler<PlaybackEnded>? Ended;

    /// <summary>Replaces what plays with <paramref name="source"/>; returns the new load's id (1, 2, ...).</summary>
    long Load(MediaSource source);

    void SetPaused(bool paused);

    void Seek(TimeSpan position);

    /// <summary>Sets the volume in percent; values outside 0 to 100 are clamped.</summary>
    void SetVolume(double percent);

    void SetMuted(bool muted);

    /// <summary>
    /// Switches what plays to its audio stream <paramref name="stream"/>, counted from 1 in the file's order. False
    /// when the switch did not take, for one when the file has no such stream.
    /// </summary>
    bool SetAudioStream(int stream);

    /// <summary>
    /// Reads the subtitle file at <paramref name="url"/> into load <paramref name="loadId"/> without showing it. Answers
    /// its subtitle stream for <see cref="SetSubtitleStream"/>, or null when it could not be read or another load plays
    /// by now. Reading may take a while (the server can extract the track first), so it runs off the caller's thread.
    /// </summary>
    Task<int?> AddSubtitleAsync(Uri url, long loadId);

    /// <summary>The subtitle stream that is the file's FFmpeg stream <paramref name="ffmpegIndex"/>; null when there is none.</summary>
    int? SubtitleStreamOf(int ffmpegIndex);

    /// <summary>Shows subtitle stream <paramref name="stream"/>, counted from 1; 0 shows none. False when it did not take.</summary>
    bool SetSubtitleStream(int stream);

    /// <summary>Shows subtitles this much later (negative: earlier) than their own times say.</summary>
    void SetSubtitleDelay(double seconds);

    /// <summary>Styles text subtitles from now on; image subtitles and styled ASS keep their own look.</summary>
    void SetSubtitleLook(SubtitleAppearance look);

    /// <summary>
    /// Raises subtitles at the bottom by <paramref name="fraction"/> of the picture's height, 0 to 0.5, so they clear
    /// controls drawn over it; 0 puts them back.
    /// </summary>
    void SetSubtitleLift(double fraction);

    void Stop();
}
