using Granary.Core.Api.Playback;
using Granary.Core.Playback;

namespace Granary.Tests.Fakes;

/// <summary>Records what the player asks of a server playback; the test sets its title, timeline and status.</summary>
internal sealed class FakeServerPlayback : IServerPlayback
{
    public event EventHandler<ServerPlaybackStatus>? StatusChanged;

    public event EventHandler? TracksChanged;

    public event EventHandler? MarkersChanged;

    public ServerPlaybackStatus Status { get; private set; } = new(ServerPlaybackPhase.Preparing);

    public string Title { get; set; } = "";

    public string Subtitle { get; set; } = "";

    public double TimelineOffsetSeconds { get; set; }

    public double? DurationSeconds { get; set; }

    public PlaybackPreferences Preferences { get; set; } = PlaybackPreferences.Default;

    public PlayingEpisode? Episode { get; set; }

    /// <summary>Every auto-play switch the player passed on, in order.</summary>
    public List<bool> AutoPlaySwitches { get; } = [];

    public void SetAutoPlayNext(bool enabled)
    {
        AutoPlaySwitches.Add(enabled);
        Preferences = Preferences with { Markers = Preferences.Markers with { AutoPlayNext = enabled } };
    }

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

    /// <summary>Every stored subtitle the player said was added, in order.</summary>
    public List<string> AddedSubtitles { get; } = [];

    public void ShowAddedSubtitle(string storedSubtitleId) => AddedSubtitles.Add(storedSubtitleId);

    public int SubtitleDelayMs { get; set; }

    public void SetSubtitleDelay(int milliseconds) => SubtitleDelayMs = Math.Clamp(milliseconds, -10_000, 10_000);

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

    public IReadOnlyList<PlaybackChapter> Chapters { get; set; } = [];

    public PlaybackStreamInfo? StreamInfo { get; set; }

    public PlayerTrickplay? Trickplay { get; set; }

    public event EventHandler? TrickplayChanged;

    public void RaiseTrickplay(PlayerTrickplay? trickplay)
    {
        Trickplay = trickplay;
        TrickplayChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Plays a file with <paramref name="chapters"/>, as a newly taken plan does.</summary>
    public void RaiseChapters(IReadOnlyList<PlaybackChapter> chapters)
    {
        Chapters = chapters;
        TracksChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<AvailableQuality> Qualities { get; set; } = [];

    public string QualityPreference { get; set; } = "auto";

    public string? ActiveQuality { get; set; }

    public bool IsBandwidthLimited { get; set; }

    /// <summary>Every quality the player asked for, in order.</summary>
    public List<string> QualityPicks { get; } = [];

    public void SelectQuality(string preference) => QualityPicks.Add(preference);

    public IReadOnlyList<WatchFileVersion> Versions { get; set; } = [];

    public string? PlayingFileId { get; set; }

    public string? RequestedFileId { get; set; }

    /// <summary>Every file the player switched to, in order.</summary>
    public List<string> VersionPicks { get; } = [];

    public void SelectVersion(string fileId) => VersionPicks.Add(fileId);

    public double Speed { get; set; } = 1;

    public void SetSpeed(double speed) => Speed = speed;

    /// <summary>Takes a new plan's menu, as TracksChanged reports it.</summary>
    public void RaisePlan(IReadOnlyList<AvailableQuality> qualities, string preference, string? active)
    {
        Qualities = qualities;
        QualityPreference = preference;
        ActiveQuality = active;
        TracksChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<PlaybackMarker> Markers { get; set; } = [];

    /// <summary>Names <paramref name="markers"/> for the playing file, as a read that found more does.</summary>
    public void RaiseMarkers(IReadOnlyList<PlaybackMarker> markers)
    {
        Markers = markers;
        MarkersChanged?.Invoke(this, EventArgs.Empty);
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
