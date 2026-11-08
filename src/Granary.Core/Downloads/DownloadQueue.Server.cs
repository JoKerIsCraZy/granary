using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>
/// The queue and the server's registry (docs/architecture.md, "What the server says"): the capability read at each scope
/// activation, the registry reconciled with what the PC keeps, preparing copies polled, refusals, completed downloads'
/// manifests refreshed, and the deletes the server still has to hear.
/// </summary>
public sealed partial class DownloadQueue
{
    /// <summary>How often the registry is read again while a copy of the scope is being prepared on the server.</summary>
    public static readonly TimeSpan PreparingPoll = TimeSpan.FromSeconds(10);

    /// <summary>How old a completed download's manifest gets before a scope activation reads it again.</summary>
    public static readonly TimeSpan ManifestRefresh = TimeSpan.FromHours(24);

    // The most pages a reconcile reads; a registry that goes on is not judged.
    private const int MaxPages = 1000;

    /// <summary>
    /// Reads the whole registry of the active scope and brings what the PC keeps in line: preparing entries and their progress,
    /// entries ready again, new revisions (their files replaced), revoked, failed and cancelled entries, and, once every page is
    /// read, downloads the server no longer lists. A registry that cannot be read changes nothing.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        DownloadScope? scope;
        Uri? server;
        lock (_gate)
        {
            (scope, server) = _current;
        }
        if (scope is not null && server is not null)
        {
            await ReconcileAsync(scope, server, cancellationToken).ConfigureAwait(false);
        }
    }

    // A scope becomes active (a start, a profile switch, a sign-in back): what the server offers and what it lists, before any
    // of its transfers starts; the completed downloads' manifests that are a day old are left to the server work.
    private async Task ActivateAsync(DownloadScope scope, Uri server, CancellationToken stop)
    {
        _logger.LogInformation("The downloads of the active profile run");
        await ReadCapabilityAsync(scope, server, stop).ConfigureAwait(false);
        await ReconcileAsync(scope, server, stop).ConfigureAwait(false);
        lock (_gate)
        {
            _refreshFor = scope;
        }
    }

    // A capability that cannot be read asks /file and never deletes a revoked copy.
    private async Task ReadCapabilityAsync(DownloadScope scope, Uri server, CancellationToken stop)
    {
        Capability capability;
        try
        {
            var read = await _api.GetCapabilityAsync(server, scope.ProfileId, stop).ConfigureAwait(false);
            capability = new Capability(scope, read.ProxyDelivery, read.RevokedRemovesLocal);
        }
        catch (Exception ex) when (IsServerFailure(ex, stop))
        {
            _logger.LogInformation("The downloads capability could not be read ({Failure})", Describe(ex));
            capability = new Capability(scope, ViaProxy: false, RevokedRemovesLocal: false);
        }
        lock (_gate)
        {
            _capability = capability;
        }
    }

    private bool IsReconcileDue(DownloadScope scope)
    {
        var now = _time.GetUtcNow();
        bool preparing = _store.In(scope).Any(d => !d.FilesMissing && d.Record.State == DownloadState.Preparing);
        lock (_gate)
        {
            return IsReconcileDueLocked(preparing, now);
        }
    }

    // Asked for and not waiting out its delay after a read that failed, or a preparing copy to look at again.
    private bool IsReconcileDueLocked(bool preparing, DateTimeOffset now) =>
        (_reconcileRequested && (_reconcileRetry is not { } retry || retry.At <= now))
        || (preparing && (_lastReconcile is not { } last || now - last >= PreparingPoll));

    private void RequestReconcile()
    {
        lock (_gate)
        {
            _reconcileRequested = true;
            WakeLocked();
        }
    }

    private async Task ReconcileAsync(DownloadScope scope, Uri server, CancellationToken cancellationToken)
    {
        await _reconciling.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Only downloads kept before the registry is read are judged: one added meanwhile may be missing from it.
            var judged = _store.In(scope).Where(d => !d.FilesMissing).Select(d => d.Record.DownloadId).ToList();
            bool asked;
            lock (_gate)
            {
                asked = _reconcileRequested;
                _reconcileRequested = false;
                _lastReconcile = _time.GetUtcNow();
            }
            var (entries, complete) = await ListAllAsync(scope, server, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                // A read that was asked for (a refusal, a report the server did not take) and failed is asked for again, after
                // the retry delays.
                if (entries is null && asked)
                {
                    int failures = _reconcileRetry?.Failures ?? 0;
                    _reconcileRequested = true;
                    _reconcileRetry = new Retry(_time.GetUtcNow() + RetryDelays[Math.Min(failures, RetryDelays.Count - 1)], failures + 1);
                }
                else if (entries is not null)
                {
                    _reconcileRetry = null;
                }
            }
            if (entries is null)
            {
                return;
            }
            foreach (string id in judged)
            {
                if (_store.Find(scope, id) is not { FilesMissing: false } download)
                {
                    continue;
                }
                if (entries.TryGetValue(id, out var entry))
                {
                    await ApplyAsync(scope, server, download, entry, cancellationToken).ConfigureAwait(false);
                }
                else if (complete)
                {
                    await NoLongerListedAsync(download).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            try
            {
                _reconciling.Release();
            }
            catch (ObjectDisposedException)
            {
                // The queue was disposed meanwhile: there is no next reconcile to let in.
            }
        }
    }

    // Every page of the registry by its cursor; null when it could not be read, not complete when it did not end (no cursor, a
    // cursor that came back, or more than MaxPages pages).
    private async Task<(Dictionary<string, DownloadEntry>? Entries, bool Complete)> ListAllAsync(DownloadScope scope, Uri server,
        CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, DownloadEntry>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        try
        {
            for (int page = 0; page < MaxPages; page++)
            {
                var read = await _api.ListAsync(server, scope.ProfileId, cursor, cancellationToken).ConfigureAwait(false);
                foreach (var entry in read.Items)
                {
                    entries.TryAdd(entry.Id, entry);
                }
                if (read.Page is not { HasMore: true })
                {
                    return (entries, true);
                }
                if (read.Page.NextCursor is not { Length: > 0 } next || !cursors.Add(next))
                {
                    break;
                }
                cursor = next;
            }
            _logger.LogWarning("The download registry did not end; nothing counts as gone from it");
            return (entries, false);
        }
        catch (Exception ex) when (IsServerFailure(ex, cancellationToken))
        {
            _logger.LogInformation("The download registry could not be read ({Failure})", Describe(ex));
            return (null, false);
        }
    }

    private async Task ApplyAsync(DownloadScope scope, Uri server, StoredDownload download, DownloadEntry entry, CancellationToken cancellationToken)
    {
        long revision = entry.Revision;
        if (entry.Revision > download.Record.Revision)
        {
            if (entry.Status is DownloadStatuses.Revoked or DownloadStatuses.Failed or DownloadStatuses.Cancelled)
            {
                // New bytes that will never come: the copy on this PC is not deleted for them, and takes the status as it is.
                revision = download.Record.Revision;
            }
            else if (await ReplaceAsync(download, entry).ConfigureAwait(false) is { } replaced)
            {
                download = replaced;
            }
            else
            {
                return;
            }
        }
        switch (entry.Status)
        {
            case DownloadStatuses.Revoked when RemovesRevoked(scope):
                await RemoveRevokedAsync(scope, server, download, cancellationToken).ConfigureAwait(false);
                break;
            case DownloadStatuses.Revoked or DownloadStatuses.Cancelled:
                // Kept playable, and no longer fetched.
                await UpdateAsync(download, revision, r => r.State == DownloadState.Completed
                    ? r with { IsNoLongerAvailable = true }
                    : r with { State = DownloadState.Failed, Failure = DownloadFailure.Server, IsNoLongerAvailable = true }).ConfigureAwait(false);
                break;
            case DownloadStatuses.Failed:
                await UpdateAsync(download, revision, r => IsUnderWay(r) ? r with { State = DownloadState.Failed, Failure = DownloadFailure.Server } : r)
                    .ConfigureAwait(false);
                break;
            case DownloadStatuses.Preparing:
                double? progress = entry.Preparation is { Progress: >= 0 and <= 1 } preparation ? preparation.Progress : null;
                await UpdateAsync(download, revision, r => IsUnderWay(r) ? r with { State = DownloadState.Preparing, PreparationProgress = progress } : r)
                    .ConfigureAwait(false);
                break;
            case DownloadStatuses.Ready or DownloadStatuses.Downloading or DownloadStatuses.Completed:
                // A ready entry's size is exact; a preparing one's was an estimate.
                await UpdateAsync(download, revision, r => r.State is DownloadState.Preparing or DownloadState.Queued
                    ? r with
                    {
                        State = DownloadState.Queued,
                        PreparationProgress = null,
                        ExpectedBytes = entry.FileSize > 0 ? entry.FileSize : r.ExpectedBytes,
                    }
                    : r).ConfigureAwait(false);
                break;
        }
    }

    // A download the server still prepares, queues or sends.
    private static bool IsUnderWay(DownloadRecord record) => record.State is DownloadState.Preparing or DownloadState.Queued or DownloadState.Downloading;

    private bool RemovesRevoked(DownloadScope scope)
    {
        lock (_gate)
        {
            return _capability is { RevokedRemovesLocal: true } capability && capability.Scope == scope;
        }
    }

    // New bytes on the server: the transfer of the old ones stops and lets go of its part, then the media, the part and the
    // manifest go, and the download is queued again for the new revision with its subtitles to fetch again.
    private async Task<StoredDownload?> ReplaceAsync(StoredDownload download, DownloadEntry entry)
    {
        var key = Key.Of(download.Record);
        Task? running = null;
        lock (_gate)
        {
            _resetting.Add(key);
            if (_running.TryGetValue(key, out var run))
            {
                CancelLocked(run);
                running = run.Task;
            }
        }
        try
        {
            if (running is not null)
            {
                await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            _logger.LogInformation("Download {DownloadId} has new bytes on the server (revision {Revision}); its files are fetched again",
                key.Id, entry.Revision);
            foreach (string? file in new[] { download.MediaPath, download.PartPath, Path.Combine(download.Folder, DownloadPaths.Manifest) })
            {
                TryDelete(file, key);
            }
            var replaced = await _store.UpdateAsync(key.Scope, key.Id, r => r with
            {
                Revision = entry.Revision,
                State = DownloadState.Queued,
                Failure = DownloadFailure.None,
                IsNoLongerAvailable = false,
                MediaFile = null,
                Validator = null,
                ManifestReadAt = null,
                ExpectedBytes = entry.FileSize > 0 ? entry.FileSize : r.ExpectedBytes,
                PreparationProgress = null,
                PendingReport = null,
                NextReport = null,
                SubtitleRevisions = new Dictionary<string, string>(),
            }, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                _bytes[key] = 0;
                _retries.Remove(key);
                _integrityFailed.Remove(key);
                _resumeWithMargin.Remove(key);
            }
            return replaced;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        finally
        {
            lock (_gate)
            {
                // A pass that passed it over meanwhile does not look again by itself.
                _resetting.Remove(key);
                WakeLocked();
            }
        }
    }

    private void TryDelete(string? file, Key key)
    {
        try
        {
            if (file is not null)
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("A file of download {DownloadId} could not be deleted ({Failure})", key.Id, DownloadLog.Failure(ex));
        }
    }

    // Revoked, and the server asks the PC to delete its copy: the files go first, then the entry (queued when it cannot go now).
    private async Task RemoveRevokedAsync(DownloadScope scope, Uri server, StoredDownload download, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Download {DownloadId} was revoked on the server; its files are deleted", download.Record.DownloadId);
        await _store.RemoveAsync(scope, download.Record.DownloadId, queueServerDelete: true, cancellationToken).ConfigureAwait(false);
        await SendPendingDeletesAsync(scope, server, cancellationToken).ConfigureAwait(false);
    }

    // Gone from a whole registry: one still to fetch can never be, one on the PC stays as it is.
    private Task NoLongerListedAsync(StoredDownload download) =>
        UpdateAsync(download, download.Record.Revision, r => r.State == DownloadState.Completed
            ? r
            : r with { State = DownloadState.Failed, Failure = DownloadFailure.Server, IsNoLongerAvailable = true });

    // Changes a download the registry spoke of, while it is still of that revision; one deleted meanwhile is no change.
    private async Task UpdateAsync(StoredDownload download, long revision, Func<DownloadRecord, DownloadRecord> change)
    {
        try
        {
            await _store.UpdateAsync(download.Record.Scope, download.Record.DownloadId, r => r.Revision == revision ? change(r) : r,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            // Deleted meanwhile.
        }
    }

    // A completed download's manifest a day old is read again at a scope activation, with the artwork it lacks and the
    // subtitles whose revision changed. One that does not come is tried at the next activation.
    private async Task RefreshManifestsAsync(DownloadScope scope, Uri server, CancellationToken stop)
    {
        var now = _time.GetUtcNow();
        var due = _store.In(scope).Where(d => !d.FilesMissing && d.Record is { State: DownloadState.Completed, IsNoLongerAvailable: false }
            && (d.Record.ManifestReadAt is not { } read || now - read >= ManifestRefresh));
        foreach (var download in due)
        {
            try
            {
                var fetched = await _assets.FetchAsync(server, download, stop).ConfigureAwait(false);
                await UpdateAsync(download, download.Record.Revision, r => r with
                {
                    ManifestReadAt = now,
                    ArtworkFiles = fetched.ArtworkFiles,
                    SubtitleRevisions = fetched.SubtitleRevisions,
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsServerFailure(ex, stop) || ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogInformation("The manifest of download {DownloadId} could not be read again ({Failure})", download.Record.DownloadId, Describe(ex));
                if (ex is HttpRequestException or ProfileChangedException)
                {
                    return;
                }
            }
        }
    }

    // A refusal from the file route. An expired prepared file is prepared again, once for this answer; a revoked or missing
    // entry fails and the registry is read to tell what became of it.
    private async Task OnServerRefusalAsync(Running run, StoredDownload download, TransferOutcome outcome, CancellationToken stop)
    {
        var key = run.Key;
        if (outcome.Result == TransferResult.Expired)
        {
            try
            {
                await _api.PrepareAsync(run.Server, key.Scope.ProfileId, key.Id, stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsServerFailure(ex, stop))
            {
                var (result, retryAfter) = ResultOf(ex);
                _logger.LogInformation("Download {DownloadId} could not be prepared again ({Failure}): {Result}", key.Id, Describe(ex), result);
                // Never Expired again: the outcome is a busy, unreachable or refusing server.
                await OnOutcomeAsync(run, download, outcome.Bytes, new TransferOutcome(result, outcome.Validator, retryAfter, outcome.Bytes))
                    .ConfigureAwait(false);
                return;
            }
            _logger.LogInformation("The server prepares download {DownloadId} again", key.Id);
            lock (_gate)
            {
                // The poll asks for it after PreparingPoll, not at once.
                _lastReconcile = _time.GetUtcNow();
            }
            await ChangeOwnAsync(key, run.Revision, r => r with
            {
                State = DownloadState.Preparing,
                PreparationProgress = null,
                Validator = outcome.Validator,
            }, stop).ConfigureAwait(false);
            return;
        }
        await ChangeOwnAsync(key, run.Revision, r => r with
        {
            State = DownloadState.Failed,
            Failure = DownloadFailure.Server,
            Validator = outcome.Validator,
        }, stop).ConfigureAwait(false);
        RequestReconcile();
    }

    // The server deletes of the active scope, each once its folder is gone (the files go first; a folder on a drive that is not
    // there does not hold it back): one whose folder something still holds goes after a folder recheck deleted it. Each is
    // forgotten once the server took it or will never take it. After one that could not go, they wait for the retry delays.
    private async Task SendPendingDeletesAsync(DownloadScope scope, Uri server, CancellationToken stop)
    {
        lock (_gate)
        {
            if (_deleteRetry is { } failed && failed.At > _time.GetUtcNow())
            {
                return;
            }
        }
        foreach (var delete in _store.PendingDeletes(scope).Where(d => IsSendable(scope, d)))
        {
            try
            {
                await _api.DeleteAsync(server, scope.ProfileId, delete.DownloadId, stop).ConfigureAwait(false);
            }
            catch (SiloApiException ex) when (IsFinal(ex))
            {
                _logger.LogInformation("The server did not take the delete of download {DownloadId} ({Status}); it is not sent again",
                    delete.DownloadId, ex.Status);
            }
            catch (Exception ex) when (IsServerFailure(ex, stop))
            {
                _logger.LogInformation("The delete of download {DownloadId} could not be sent ({Failure}); it is sent again", delete.DownloadId, Describe(ex));
                lock (_gate)
                {
                    int failures = _deleteRetry?.Failures ?? 0;
                    _deleteRetry = new Retry(_time.GetUtcNow() + RetryDelays[Math.Min(failures, RetryDelays.Count - 1)], failures + 1);
                }
                return;
            }
            await _store.ForgetPendingDeleteAsync(delete, stop).ConfigureAwait(false);
            lock (_gate)
            {
                _deleteRetry = null;
            }
        }
    }

    // A delete whose folder is gone, or on a drive that is not there: it reads the disk, on the loop's or the server work's thread.
    private bool IsSendable(DownloadScope scope, PendingDelete delete) =>
        _store.FolderBeingDeleted(scope, delete.DownloadId) is not { } folder || !Directory.Exists(folder);

    // A failure of a request to the server, not a bug: an answer, no answer, an answer that is no Silo answer, a profile switch
    // or a timeout of the HTTP client.
    private static bool IsServerFailure(Exception ex, CancellationToken stop) =>
        ex is HttpRequestException or SiloApiException or ProfileChangedException or JsonException
        || (ex is OperationCanceledException && !stop.IsCancellationRequested);

    // A failure as the log may name it: a status, a network error's kind, or an exception's kind and code, never a message.
    private static string Describe(Exception ex) => ex switch
    {
        SiloApiException api => api.Status.ToString(System.Globalization.CultureInfo.InvariantCulture),
        HttpRequestException http => http.HttpRequestError.ToString(),
        _ => DownloadLog.Failure(ex),
    };
}
