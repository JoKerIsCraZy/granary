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
/// missing (Android streams those without a word; the Downloads page says "Missing file" from then on), a Watch Party (the room's file plays for everyone), and a version the
/// viewer picked on the title page that is not the one downloaded.
/// </summary>
public sealed class DownloadPlaybacks(DownloadStore store, ISessionManager session, Func<LocalMedia, LocalPlayback> playbacks, TimeProvider time,
    ILogger<DownloadPlaybacks> logger)
{
    /// <summary>How long the folder may take to answer before the title streams instead: a share that does not answer holds no play up.</summary>
    public static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(3);

    /// <summary>For tests: runs on the read's thread before the folder is read, as a drive that does not answer holds it.</summary>
    internal Action? Reading { get; set; }

    // The reads of download folders under way, by folder. A read the budget gave up on runs on until the drive answers or
    // fails: the next Play or Retry of that title waits for the same read instead of holding one more thread.
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<LocalMedia?>> _reads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The active profile's download <paramref name="request"/> would play, as far as the store knows without reading the disk;
    /// null when the title streams.
    /// </summary>
    public StoredDownload? Find(PlayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContentId is not { } contentId || request.IsWatchParty || DownloadScope.Of(session) is not { } scope
            || store.FindByItem(scope, contentId) is not { CanPlay: true } download)
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
            if (await ReadOnce(download).WaitAsync(ReadBudget, time, cancellationToken) is not { } read)
            {
                return null;
            }
            // The record names the version only when the viewer chose one; the manifest always says which file came.
            if (request.Choice is { IsVersionPicked: true } choice && read.Manifest.MediaFileId is { Length: > 0 } fileId && choice.FileId != fileId)
            {
                return null;
            }
            // The read can take seconds, and a profile can be switched to in them: the download plays only for the profile it
            // was found for, as it is now (the read may be one an earlier start began).
            if (Find(request) is not { } now || now.Record.Scope != download.Record.Scope || now.Record.DownloadId != download.Record.DownloadId)
            {
                logger.LogInformation("Download {DownloadId} is no longer the active profile's to play; the title streams", download.Record.DownloadId);
                return null;
            }
            return playbacks(read with { Download = now });
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

    // The read of the download's folder: the one under way, else a new one.
    private Task<LocalMedia?> ReadOnce(StoredDownload download)
    {
        lock (_gate)
        {
            // One that has ended is not under way, though it is still listed until its continuation ran: a Play right after
            // it reads the folder as it is now, not what that read found.
            if (_reads.TryGetValue(download.Folder, out var running) && !running.IsCompleted)
            {
                return running;
            }
            var read = Task.Run(() => Read(download));
            _reads[download.Folder] = read;
            _ = read.ContinueWith(done =>
            {
                lock (_gate)
                {
                    if (_reads.TryGetValue(download.Folder, out var kept) && kept == done)
                    {
                        _reads.Remove(download.Folder);
                    }
                }
                // Whoever waited may have given up on it: what it failed with is looked at here, so it is logged and never
                // an exception nobody observed. Only its kind: the message can name the folder.
                if (done.Exception is { } failure)
                {
                    logger.LogWarning("Reading the folder of download {DownloadId} failed unexpectedly ({Failure})", download.Record.DownloadId,
                        DownloadLog.Failure(failure.GetBaseException()));
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return read;
        }
    }

    // On the thread pool: the folder can be on a drive that is slow to answer.
    private LocalMedia? Read(StoredDownload download)
    {
        string id = download.Record.DownloadId;
        try
        {
            Reading?.Invoke();
            var files = Directory.EnumerateFiles(download.Folder).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!files.Contains(download.Record.MediaFile!))
            {
                logger.LogWarning("The media file of download {DownloadId} is not in its folder; the title streams", id);
                // Its row stops saying Downloaded and offering Play, unless it names another file by now: this read may be
                // one an earlier start began, before the server's new bytes were fetched.
                store.MarkMediaMissing(download);
                return null;
            }
            // A folder that lists the file does not say it can be opened: one this account may not read, or that something
            // holds for itself, streams, as a file that is gone does.
            using (new FileStream(download.MediaPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
            }
            var manifest = OfflineManifest.Read(File.ReadAllBytes(Path.Combine(download.Folder, DownloadPaths.Manifest)));
            if (manifest.DownloadId != id)
            {
                logger.LogWarning("The manifest of download {DownloadId} is another download's; the title streams", id);
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
