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

    /// <summary>
    /// The server will not let this profile download it (403, 404, 501, or passed over), or no profile can download now: none
    /// is signed in, or its account is being signed out.
    /// </summary>
    Unavailable,

    /// <summary>The server could not be asked, or did not say what it made.</summary>
    Failed,
}

/// <summary>A season to download, one episode at a time.</summary>
public sealed record SeasonDownloadRequest(string SeriesId, string SeriesTitle, int SeasonNumber, IReadOnlyList<SeasonEpisode> Episodes, string Quality);

/// <param name="MediaFileId">The file to download, the one the dialog's size counted; null lets the server choose.</param>
public sealed record SeasonEpisode(string EpisodeId, int EpisodeNumber, string Title, string? MediaFileId = null);

/// <summary>How many episodes of a season started, were there already, or could not start.</summary>
public sealed record SeasonStartResult(int Started, int AlreadyThere, int Failed);

/// <summary>
/// What <see cref="DownloadManager.Changed"/> carries when only a transfer went further: the download it is, so a page can show
/// that row's bytes (<see cref="DownloadManager.BytesDone(string)"/>) without reading every download again.
/// </summary>
public sealed class DownloadProgressedEventArgs(string downloadId) : EventArgs
{
    public string DownloadId { get; } = downloadId;
}

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
    private readonly PlayingDownloads? _playing;
    private readonly TimeProvider _time;
    private readonly ILogger<DownloadManager> _logger;

    // The titles whose create is under way, by scope: a second press while one runs creates nothing.
    private readonly Lock _gate = new();
    private readonly HashSet<(DownloadScope Scope, string MediaItemId)> _starting = [];

    public DownloadManager(DownloadStore store, DownloadQueue queue, DownloadsApi api, DownloadsFeature feature, ISessionManager session,
        IPlaybackDeviceProbe probe, IPlaybackDevicePreferences device, TimeProvider time, ILogger<DownloadManager> logger,
        PlayingDownloads? playing = null)
    {
        ArgumentNullException.ThrowIfNull(feature);
        _store = store;
        _queue = queue;
        _api = api;
        _session = session;
        _probe = probe;
        _device = device;
        _playing = playing;
        _time = time;
        _logger = logger;
        // The manager lives as long as the app, as the store, the queue and the feature do.
        store.Changed += OnChanged;
        // A download that plays is not suggested for deletion; the suggestion follows its start and its stop.
        if (playing is not null)
        {
            playing.Changed += OnChanged;
        }
        queue.Progressed += OnProgressed;
        // "Paused · metered connection" comes and goes with the link and Settings → Downloads.
        queue.ConditionsChanged += OnChanged;
        feature.PropertyChanged += OnFeatureChanged;
    }

    /// <summary>
    /// Raised on any thread when a download changed, went further, started or stopped playing, or what the server offers did,
    /// and when downloads start or stop waiting for an unmetered connection. A transfer that only went further comes with
    /// <see cref="DownloadProgressedEventArgs"/>, at most every half second for each.
    /// </summary>
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

    /// <summary>One of the active profile's downloads by its own id; null when it is not there.</summary>
    public DownloadItem? Find(string downloadId) =>
        DownloadScope.Of(_session) is { } scope && _store.Find(scope, downloadId) is { } download ? ItemOf(download, _queue.IsWaitingForUnmetered) : null;

    /// <summary>
    /// The bytes one of the active profile's downloads has on this PC, as <see cref="DownloadItem.BytesDone"/> counts them; 0
    /// when it is not there. It reads neither the disk nor the connection, so a page can follow a transfer with it.
    /// </summary>
    public long BytesDone(string downloadId) =>
        DownloadScope.Of(_session) is { } scope && _store.Find(scope, downloadId) is { } download ? BytesDone(scope, download.Record) : 0;

    /// <summary>
    /// How fast one of the active profile's downloads comes now, in bytes a second, over its transfer's last seconds; null
    /// while it does not come, before there is enough to measure, and once its bytes stopped coming. It reads neither the
    /// disk nor the connection.
    /// </summary>
    public double? BytesPerSecond(string downloadId) =>
        DownloadScope.Of(_session) is { } scope ? _queue.BytesPerSecond(scope, downloadId) : null;

    /// <summary>
    /// The film or episode one of the active profile's downloads is, by the download's id; null when it is not there. It reads
    /// neither the disk nor the connection, so a page can tell whether a transfer's progress is one it shows.
    /// </summary>
    public string? MediaItemIdOf(string downloadId) =>
        DownloadScope.Of(_session) is { } scope && _store.Find(scope, downloadId) is { } download ? download.Record.MediaItemId : null;

    /// <summary>The bytes the active profile's downloads take: the finished files and the parts so far, as the queue knows them.</summary>
    public long BytesUsed() =>
        DownloadScope.Of(_session) is { } scope
            ? _store.In(scope).Where(d => !d.FilesMissing && !d.MediaMissing).Sum(d => BytesDone(scope, d.Record))
            : 0;

    /// <summary>
    /// Has the downloads' folders looked at again soon, off the caller's thread: a page that lists them shows a file that was
    /// deleted by hand, or a drive that came back, without waiting for the queue's next look.
    /// </summary>
    public void LookAtFiles() => _queue.RecheckSoon();

    /// <summary>Pauses a download until the viewer resumes it, also across starts; its transfer stops, keeping its bytes.</summary>
    public async Task PauseAsync(string downloadId)
    {
        if (DownloadScope.Of(_session) is { } scope)
        {
            await ChangeAsync(scope, downloadId, r => r.State is DownloadState.Preparing or DownloadState.Queued or DownloadState.Downloading
                ? r with { IsPausedByViewer = true }
                : r).ConfigureAwait(false);
            _queue.Wake();
        }
    }

    /// <summary>
    /// Resumes a paused download, and tries a failed one again from the bytes it has; one that waited to be tried again after a
    /// lost connection or a busy server is tried at once.
    /// </summary>
    public async Task ResumeAsync(string downloadId)
    {
        if (DownloadScope.Of(_session) is { } scope)
        {
            await ChangeAsync(scope, downloadId, r => r.State == DownloadState.Failed && !r.IsNoLongerAvailable
                ? r with { IsPausedByViewer = false, State = DownloadState.Queued, Failure = DownloadFailure.None }
                : r with { IsPausedByViewer = false }).ConfigureAwait(false);
            _queue.RetryNow(scope, downloadId);
        }
    }

    /// <summary>
    /// Deletes a download from this PC: its transfer stops and its files go first, then the server hears it, bound to the
    /// download's profile. While the server cannot be reached, the queue keeps the delete and sends it once it can.
    /// </summary>
    public async Task DeleteAsync(string downloadId, CancellationToken cancellationToken)
    {
        if (DownloadScope.Of(_session) is { } scope)
        {
            await DeleteAsync(scope, downloadId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DeleteAsync(DownloadScope scope, string downloadId, CancellationToken cancellationToken)
    {
        await _store.RemoveAsync(scope, downloadId, queueServerDelete: true, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Download {DownloadId} was deleted from this PC", downloadId);
        _queue.Wake();
    }

    /// <summary>
    /// Deletes those of <paramref name="downloadIds"/> that are still whole and watched (<see cref="DownloadItem.IsReclaimable"/>),
    /// as <see cref="DeleteAsync"/> deletes one, and answers how many went. The ids are the ones the viewer was asked about:
    /// a download that became watched after the question stays, and so does one that is no longer watched, or that plays now:
    /// its file is never taken from under the player. They are the downloads of the profile that was active when the answer
    /// came, also when another one becomes active while they go.
    /// </summary>
    public async Task<int> DeleteWatchedAsync(IReadOnlyCollection<string> downloadIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(downloadIds);
        if (DownloadScope.Of(_session) is not { } scope)
        {
            return 0;
        }
        int deleted = 0;
        foreach (string downloadId in downloadIds)
        {
            // Looked at right before each delete, not once for all: a delete takes a while (its folder, the index), and one
            // of the others can start playing meanwhile, or stop being watched as the server's progress is read.
            if (_store.Find(scope, downloadId) is not { } download || !ItemOf(download, waitsForUnmetered: false).IsReclaimable)
            {
                continue;
            }
            await DeleteAsync(scope, downloadId, cancellationToken).ConfigureAwait(false);
            deleted++;
        }
        return deleted;
    }

    private async Task ChangeAsync(DownloadScope scope, string downloadId, Func<DownloadRecord, DownloadRecord> change)
    {
        try
        {
            await _store.UpdateAsync(scope, downloadId, change, CancellationToken.None).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            // Deleted meanwhile.
        }
    }

    private DownloadItem ItemOf(StoredDownload download, bool waitsForUnmetered)
    {
        var record = download.Record;
        // A finished download whose media file was deleted by hand is missing its file too, for all a page shows of it.
        return new DownloadItem(record.DownloadId, record.MediaItemId, record.Title, record.SeriesId, record.SeriesTitle, record.SeasonNumber,
            record.EpisodeNumber, record.State, record.Failure, record.IsPausedByViewer, record.IsNoLongerAvailable,
            download.FilesMissing || download.MediaMissing,
            waitsForUnmetered, record.ExpectedBytes, BytesDone(record.Scope, record), record.PreparationProgress, ArtworkPath(download, "poster"),
            record.MediaFileId, ArtworkPath(download, "series_poster"), record.IsWatched,
            _playing?.Contains(record.Scope, record.DownloadId) == true);
    }

    // The record names its artwork, so nothing is read from the disk; none while the folder is missing.
    private static string? ArtworkPath(StoredDownload download, string kind) =>
        !download.FilesMissing && download.Record.ArtworkFiles.TryGetValue(kind, out string? name) ? Path.Combine(download.Folder, name) : null;

    private long BytesDone(DownloadScope scope, DownloadRecord record) =>
        record.State == DownloadState.Completed ? record.ExpectedBytes : _queue.BytesDone(scope, record.DownloadId);

    private void OnChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnProgressed(object? sender, string downloadId) => Changed?.Invoke(this, new DownloadProgressedEventArgs(downloadId));

    private void OnFeatureChanged(object? sender, PropertyChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}
