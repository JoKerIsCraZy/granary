using System.ComponentModel;
using Granary.Core.Api.Downloads;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>
/// A film or an episode to download: its own id, its series for an episode, the version the viewer chose (none: the server
/// picks one), the quality preset, and what its row shows.
/// </summary>
public sealed record DownloadRequest(string MediaItemId, string? SeriesId, string? MediaFileId, string Quality, string Title, string? SeriesTitle,
    int? SeasonNumber, int? EpisodeNumber);

public enum DownloadStartResult
{
    Started,

    /// <summary>It is on this PC or on its way already, or a press before this one is still creating it.</summary>
    AlreadyDownloading,

    /// <summary>The server will not let this profile download it (403, 404, 501, or passed over).</summary>
    Unavailable,

    /// <summary>The server could not be asked, or did not say what it made.</summary>
    Failed,
}

/// <summary>A season to download, one episode at a time.</summary>
public sealed record SeasonDownloadRequest(string SeriesId, string SeriesTitle, int SeasonNumber, IReadOnlyList<SeasonEpisode> Episodes, string Quality);

public sealed record SeasonEpisode(string EpisodeId, int EpisodeNumber, string Title);

/// <summary>How many episodes of a season started, were there already, or could not start.</summary>
public sealed record SeasonStartResult(int Started, int AlreadyThere, int Failed);

/// <summary>
/// What the view models do with downloads (docs/architecture.md, "Starting and deleting"): the active profile's downloads as
/// <see cref="DownloadItem"/>s, starting a film, an episode or a season, pausing, resuming and deleting. The queue does the
/// transfers and sends the server's deletes.
/// </summary>
public sealed partial class DownloadManager
{
    private readonly DownloadStore _store;
    private readonly DownloadQueue _queue;
    private readonly DownloadsApi _api;
    private readonly ISessionManager _session;
    private readonly IPlaybackDeviceProbe _probe;
    private readonly IPlaybackDevicePreferences _device;
    private readonly TimeProvider _time;
    private readonly ILogger<DownloadManager> _logger;

    // The titles whose create is under way, by scope: a second press while one runs creates nothing.
    private readonly Lock _gate = new();
    private readonly HashSet<(DownloadScope Scope, string MediaItemId)> _starting = [];

    public DownloadManager(DownloadStore store, DownloadQueue queue, DownloadsApi api, DownloadsFeature feature, ISessionManager session,
        IPlaybackDeviceProbe probe, IPlaybackDevicePreferences device, TimeProvider time, ILogger<DownloadManager> logger)
    {
        ArgumentNullException.ThrowIfNull(feature);
        _store = store;
        _queue = queue;
        _api = api;
        _session = session;
        _probe = probe;
        _device = device;
        _time = time;
        _logger = logger;
        // The manager lives as long as the app, as the store, the queue and the feature do.
        store.Changed += OnChanged;
        queue.Progressed += OnProgressed;
        feature.PropertyChanged += OnFeatureChanged;
    }

    /// <summary>Raised on any thread when a download changed, went further, or what the server offers did.</summary>
    public event EventHandler? Changed;

    /// <summary>The active profile's downloads, newest first.</summary>
    public IReadOnlyList<DownloadItem> Items
    {
        get
        {
            if (DownloadScope.Of(_session) is not { } scope)
            {
                return [];
            }
            bool waits = _queue.IsWaitingForUnmetered;
            return [.. _store.In(scope).OrderByDescending(d => d.Record.CreatedAt).Select(d => ItemOf(d, waits))];
        }
    }

    /// <summary>The active profile's download of a film or an episode, by its own id.</summary>
    public DownloadItem? ForItem(string mediaItemId) =>
        DownloadScope.Of(_session) is { } scope && _store.FindByItem(scope, mediaItemId) is { } download ? ItemOf(download, _queue.IsWaitingForUnmetered) : null;

    /// <summary>The bytes the active profile's downloads take: the finished files and the parts so far, as the queue knows them.</summary>
    public long BytesUsed() =>
        DownloadScope.Of(_session) is { } scope
            ? _store.In(scope).Where(d => !d.FilesMissing).Sum(d => BytesDone(scope, d.Record))
            : 0;

    /// <summary>Pauses a download until the viewer resumes it, also across starts; its transfer stops, keeping its bytes.</summary>
    public Task PauseAsync(string downloadId) =>
        ChangeAsync(downloadId, r => r.State is DownloadState.Preparing or DownloadState.Queued or DownloadState.Downloading
            ? r with { IsPausedByViewer = true }
            : r);

    /// <summary>Resumes a paused download, and tries a failed one again from the bytes it has.</summary>
    public Task ResumeAsync(string downloadId) =>
        ChangeAsync(downloadId, r => r.State == DownloadState.Failed && !r.IsNoLongerAvailable
            ? r with { IsPausedByViewer = false, State = DownloadState.Queued, Failure = DownloadFailure.None }
            : r with { IsPausedByViewer = false });

    /// <summary>
    /// Deletes a download from this PC: its transfer stops and its files go first, then the server hears it, bound to the
    /// download's profile. While the server cannot be reached, the queue keeps the delete and sends it once it can.
    /// </summary>
    public async Task DeleteAsync(string downloadId, CancellationToken cancellationToken)
    {
        if (DownloadScope.Of(_session) is not { } scope)
        {
            return;
        }
        await _store.RemoveAsync(scope, downloadId, queueServerDelete: true, cancellationToken);
        _logger.LogInformation("Download {DownloadId} was deleted from this PC", downloadId);
        _queue.Wake();
    }

    private async Task ChangeAsync(string downloadId, Func<DownloadRecord, DownloadRecord> change)
    {
        if (DownloadScope.Of(_session) is not { } scope)
        {
            return;
        }
        try
        {
            await _store.UpdateAsync(scope, downloadId, change, CancellationToken.None);
        }
        catch (KeyNotFoundException)
        {
            // Deleted meanwhile.
        }
        _queue.Wake();
    }

    private DownloadItem ItemOf(StoredDownload download, bool waitsForUnmetered)
    {
        var record = download.Record;
        string? poster = !download.FilesMissing && record.ArtworkFiles.TryGetValue("poster", out string? name) ? Path.Combine(download.Folder, name) : null;
        return new DownloadItem(record.DownloadId, record.MediaItemId, record.Title, record.SeriesId, record.SeriesTitle, record.SeasonNumber,
            record.EpisodeNumber, record.State, record.Failure, record.IsPausedByViewer, record.IsNoLongerAvailable, download.FilesMissing,
            waitsForUnmetered, record.ExpectedBytes, BytesDone(record.Scope, record), record.PreparationProgress, poster, record.MediaFileId);
    }

    private long BytesDone(DownloadScope scope, DownloadRecord record) =>
        record.State == DownloadState.Completed ? record.ExpectedBytes : _queue.BytesDone(scope, record.DownloadId);

    private void OnChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnProgressed(object? sender, string downloadId) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnFeatureChanged(object? sender, PropertyChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}
