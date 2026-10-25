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

    /// <summary>"S01E05 · Episode title" for an episode, the year for a movie; may be empty.</summary>
    string Subtitle { get; }

    /// <summary>Added to the engine's position to get the title's own time (source time).</summary>
    double TimelineOffsetSeconds { get; }

    /// <summary>The title's length in source time, when the server knows it.</summary>
    double? DurationSeconds { get; }

    /// <summary>The profile's playback settings; the defaults until they are read.</summary>
    PlaybackPreferences Preferences { get; }

    Task StartAsync(PlayRequest request, CancellationToken cancellationToken);

    /// <summary>Moves to <paramref name="sourceSeconds"/>, asking the server for a new plan when the stream cannot get there itself.</summary>
    void Seek(double sourceSeconds);

    /// <summary>Reports the final position and ends the server session; safe to call more than once.</summary>
    Task StopAsync();
}
