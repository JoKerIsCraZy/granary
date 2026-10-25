using Granary.Core.Api.Playback;

namespace Granary.Core.Playback;

public enum ServerPlaybackPhase
{
    /// <summary>Asking the server for a plan and loading it.</summary>
    Preparing,

    Playing,

    /// <summary>The connection dropped after playback began; the picture holds while it comes back.</summary>
    Reconnecting,

    /// <summary>Playback cannot go on; <see cref="ServerPlaybackStatus.Message"/> says why.</summary>
    Failed,

    /// <summary>The title played to its end; the server has its final position.</summary>
    Ended,
}

public sealed record ServerPlaybackStatus(ServerPlaybackPhase Phase, string Message = "", bool CanRetry = false);

/// <summary>
/// One playback of a server title (docs/api-contract.md, 8 to 10): it asks the server for a plan, loads it
/// into the engine with the media headers, reports progress, recovers what the protocol lets it recover,
/// and stops the session at the end. The engine's own state (pause, volume, position) stays with the engine.
/// </summary>
/// <remarks>Used on the UI thread; <see cref="StatusChanged"/> is raised there.</remarks>
public interface IServerPlayback : IAsyncDisposable
{
    event EventHandler<ServerPlaybackStatus>? StatusChanged;

    ServerPlaybackStatus Status { get; }

    /// <summary>The movie, or the series of an episode; empty until the title is known.</summary>
    string Title { get; }

    /// <summary>"S1 · E5 — Episode title" for an episode, the year for a movie; may be empty.</summary>
    string Subtitle { get; }

    /// <summary>Added to the engine's position to get the title's own time (source time).</summary>
    double TimelineOffsetSeconds { get; }

    /// <summary>The title's length in source time, when the server knows it.</summary>
    double? DurationSeconds { get; }

    /// <summary>The profile's playback settings; the defaults until they are read.</summary>
    PlaybackPreferences Preferences { get; }

    /// <summary>The episode that plays, with its series, season and number; null for a movie and until the title is known.</summary>
    PlayingEpisode? Episode { get; }

    /// <summary>
    /// The viewer's "Auto-play is on/off": taken into <see cref="Preferences"/> at once and saved as the profile's
    /// <c>playback.auto_play_next</c>, which the web's switch and its settings page edit too.
    /// </summary>
    void SetAutoPlayNext(bool enabled);

    /// <summary>Raised on the UI thread when the file that plays or its selected tracks change.</summary>
    event EventHandler? TracksChanged;

    /// <summary>The audio tracks of the file that plays, in the file's order; empty until a plan is taken.</summary>
    IReadOnlyList<WatchAudioTrack> AudioTracks { get; }

    /// <summary>The audio track that plays, as an ordinal into <see cref="AudioTracks"/>; null when the plan names none.</summary>
    int? AudioTrack { get; }

    /// <summary>
    /// The viewer's pick: plays audio track <paramref name="ordinal"/> of <see cref="AudioTracks"/>, switching in the
    /// player when the stream holds it and asking the server for a stream that does otherwise, and saves it for the
    /// series (docs/api-contract.md, 8.8).
    /// </summary>
    void SelectAudio(int ordinal);

    /// <summary>The plan's subtitle inventory: external, then embedded, then downloaded tracks; empty until a plan is taken.</summary>
    IReadOnlyList<SubtitleInventoryItem> SubtitleTracks { get; }

    /// <summary>The inventory <c>track_id</c> of the subtitle that shows; null for none.</summary>
    string? SubtitleTrack { get; }

    /// <summary>
    /// The viewer's pick: shows inventory track <paramref name="trackId"/>, or none for null, in the player where it can
    /// and with a new plan otherwise, and saves it for the series (docs/api-contract.md, 8.8).
    /// </summary>
    void SelectSubtitle(string? trackId);

    /// <summary>The profile's subtitle delay in milliseconds, positive later; the setting's value until changed here.</summary>
    int SubtitleDelayMs { get; }

    /// <summary>
    /// Moves subtitles by <paramref name="milliseconds"/> (within ±10 s) at once, and saves it as the profile's
    /// <c>player.subtitle_sync_ms</c> once the changes settle, as Silo's web player does.
    /// </summary>
    void SetSubtitleDelay(int milliseconds);

    /// <summary>
    /// The marked parts (intro, recap, credits, preview) of the file that plays, in source seconds and start order; empty
    /// until a plan is taken. They change with the file (<see cref="TracksChanged"/>) and when the server has found more
    /// (<see cref="MarkersChanged"/>).
    /// </summary>
    IReadOnlyList<PlaybackMarker> Markers { get; }

    /// <summary>Raised on the UI thread when the server named markers of the playing file it had not named before.</summary>
    event EventHandler? MarkersChanged;

    Task StartAsync(PlayRequest request, CancellationToken cancellationToken);

    /// <summary>Moves to <paramref name="sourceSeconds"/>, asking the server for a new plan when the stream cannot get there itself.</summary>
    void Seek(double sourceSeconds);

    /// <summary>Reports the final position and ends the server session; safe to call more than once.</summary>
    Task StopAsync();
}
