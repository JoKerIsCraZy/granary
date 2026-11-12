using System.Text.Json;
using Granary.Core.Api.Downloads;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>A download as its folder holds it when a play begins: its record, its manifest, and the names of its files.</summary>
public sealed record LocalMedia(StoredDownload Download, OfflineManifest Manifest, IReadOnlySet<string> Files);

/// <summary>
/// Decides whether a title plays from this PC (docs/architecture.md, "Playing a download"). Local first, as Silo's Android
/// app plays (<c>PlayerViewModel.tryLocalPlayback</c>): a title whose download is whole on this PC plays from it, with or
/// without the server. What streams instead: a title with no finished download of the active profile, one whose files are
/// missing (Android streams those without a word), a Watch Party (the room's file plays for everyone), and a version the
/// viewer picked on the title page that is not the one downloaded.
/// </summary>
public sealed class DownloadPlaybacks(DownloadStore store, ISessionManager session, Func<LocalMedia, LocalPlayback> playbacks, TimeProvider time,
    ILogger<DownloadPlaybacks> logger)
{
    /// <summary>How long the folder may take to answer before the title streams instead: a share that does not answer holds no play up.</summary>
    public static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(3);

    /// <summary>For tests: runs on the read's thread before the folder is read, as a drive that does not answer holds it.</summary>
    internal Action? Reading { get; set; }

    /// <summary>
    /// The active profile's download <paramref name="request"/> would play, as far as the store knows without reading the disk;
    /// null when the title streams.
    /// </summary>
    public StoredDownload? Find(PlayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContentId is not { } contentId || request.IsWatchParty || DownloadScope.Of(session) is not { } scope
            || store.FindByItem(scope, contentId) is not { Record.State: DownloadState.Completed, FilesMissing: false, MediaPath: not null } download)
        {
            return null;
        }
        // A version the viewer picked themselves is the one they want; picked tracks alone stay with the download. A record
        // that does not name its version is checked against its manifest (Read).
        return request.Choice is { IsVersionPicked: true } choice && download.Record.MediaFileId is { Length: > 0 } kept && choice.FileId != kept
            ? null
            : download;
    }

    /// <summary>
    /// The playback of <paramref name="request"/> from this PC, or null when it streams: no download plays it
    /// (<see cref="Find"/>), or its manifest or media file cannot be read within <see cref="ReadBudget"/>. It answers at once,
    /// without leaving the caller's thread, when no download plays it.
    /// </summary>
    public async Task<IServerPlayback?> TryCreateAsync(PlayRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContentId is null || request.IsWatchParty)
        {
            return null;
        }
        try
        {
            // A title started before the downloads were first read (a start with --play-item) waits for that read.
            var load = store.LoadAsync(cancellationToken);
            if (!load.IsCompletedSuccessfully)
            {
                await load.WaitAsync(ReadBudget, time, cancellationToken);
            }
            if (Find(request) is not { } download)
            {
                return null;
            }
            var media = await Task.Run(() => Read(download, request), cancellationToken).WaitAsync(ReadBudget, time, cancellationToken);
            return media is null ? null : playbacks(media);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("The downloads did not answer in time; the title streams");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The downloads cannot be read ({Failure}); the title streams", DownloadLog.Failure(ex));
            return null;
        }
    }

    // On the thread pool: the folder can be on a drive that is slow to answer.
    private LocalMedia? Read(StoredDownload download, PlayRequest request)
    {
        string id = download.Record.DownloadId;
        try
        {
            Reading?.Invoke();
            var files = Directory.EnumerateFiles(download.Folder).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!files.Contains(download.Record.MediaFile!))
            {
                logger.LogWarning("The media file of download {DownloadId} is not in its folder; the title streams", id);
                return null;
            }
            var manifest = OfflineManifest.Read(File.ReadAllBytes(Path.Combine(download.Folder, DownloadPaths.Manifest)));
            if (manifest.DownloadId != id)
            {
                logger.LogWarning("The manifest of download {DownloadId} is another download's; the title streams", id);
                return null;
            }
            // The record names the version only when the viewer chose one; the manifest always says which file came.
            if (request.Choice is { IsVersionPicked: true } choice && manifest.MediaFileId is { Length: > 0 } fileId && choice.FileId != fileId)
            {
                return null;
            }
            return new LocalMedia(download, manifest, files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("Download {DownloadId} cannot be read ({Failure}); the title streams", id, DownloadLog.Failure(ex));
            return null;
        }
    }
}
