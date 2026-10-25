using Granary.Core.Api.Playback;
using Granary.Core.Playback;

namespace Granary.Tests.Fakes;

/// <summary>Records what the player asks of a server playback; the test sets its title, timeline and status.</summary>
internal sealed class FakeServerPlayback : IServerPlayback
{
    public event EventHandler<ServerPlaybackStatus>? StatusChanged;

    public event EventHandler? TracksChanged;

    public ServerPlaybackStatus Status { get; private set; } = new(ServerPlaybackPhase.Preparing);

    public string Title { get; set; } = "";

    public string Subtitle { get; set; } = "";

    public double TimelineOffsetSeconds { get; set; }

    public double? DurationSeconds { get; set; }

    public PlaybackPreferences Preferences { get; set; } = PlaybackPreferences.Default;

    public IReadOnlyList<WatchAudioTrack> AudioTracks { get; set; } = [];

    public int? AudioTrack { get; set; }

    /// <summary>Every audio pick the player passed on, in order.</summary>
    public List<int> AudioPicks { get; } = [];

    public PlayRequest? Started { get; private set; }

    public List<double> Seeks { get; } = [];

    public int Stops { get; private set; }

    /// <summary>What <see cref="StopAsync"/> returns: a pending task keeps the stop on its way.</summary>
    public Task StopResult { get; set; } = Task.CompletedTask;

    public bool Disposed { get; private set; }

    public Task StartAsync(PlayRequest request, CancellationToken cancellationToken)
    {
        Started = request;
        return Task.CompletedTask;
    }

    public void Seek(double sourceSeconds) => Seeks.Add(sourceSeconds);

    public void SelectAudio(int ordinal) => AudioPicks.Add(ordinal);

    public IReadOnlyList<SubtitleInventoryItem> SubtitleTracks { get; set; } = [];

    public string? SubtitleTrack { get; set; }

    /// <summary>Every subtitle pick the player passed on, in order; null is off.</summary>
    public List<string?> SubtitlePicks { get; } = [];

    public void SelectSubtitle(string? trackId) => SubtitlePicks.Add(trackId);

    /// <summary>Lists <paramref name="tracks"/> with <paramref name="selected"/> showing, as a newly taken plan does.</summary>
    public void RaiseSubtitles(IReadOnlyList<SubtitleInventoryItem> tracks, string? selected)
    {
        SubtitleTracks = tracks;
        SubtitleTrack = selected;
        TracksChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Plays <paramref name="tracks"/> with <paramref name="selected"/>, as a newly taken plan does.</summary>
    public void RaiseTracks(IReadOnlyList<WatchAudioTrack> tracks, int? selected)
    {
        AudioTracks = tracks;
        AudioTrack = selected;
        TracksChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task StopAsync()
    {
        Stops++;
        return StopResult;
    }

    public void Raise(ServerPlaybackStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
