using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>The queue's transfers: one download from its record to its outcome, and the status reports the server gets.</summary>
public sealed partial class DownloadQueue
{
    // One transfer. It never throws: what it does not expect is logged and tried again later.
    private async Task RunAsync(Running run)
    {
        try
        {
            await TransferAsync(run).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await AfterFailureAsync(run, ex).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _running.Remove(run.Key);
                _rates.Remove(run.Key);
                WakeLocked();
            }
            run.Cancel.Dispose();
        }
    }

    private async Task TransferAsync(Running run)
    {
        var key = run.Key;
        // The transfer's own place as the sender of its reports, taken before "downloading" is written: the server work, which
        // that write wakes, leaves it to the transfer and its budget.
        var sender = TryBeginSending(key);
        StoredDownload? download;
        try
        {
            SenderAsked?.Invoke(key.Id);
            download = await MarkDownloadingAsync(run).ConfigureAwait(false);
        }
        catch
        {
            if (sender is not null)
            {
                EndSending(key, sender);
            }
            throw;
        }
        if (download is null)
        {
            if (sender is not null)
            {
                EndSending(key, sender);
            }
            return;
        }
        await SendStartReportAsync(run, sender).ConfigureAwait(false);
        // A transfer stopped while it waited for its report asks for nothing more.
        run.Cancel.Token.ThrowIfCancellationRequested();
        (download, var failed) = await FetchAssetsFirstAsync(run, download).ConfigureAwait(false);
        long from = failed?.Bytes ?? ResumePoint(download);
        var outcome = failed ?? await _transfer.RunAsync(JobOf(run, download, from), bytes => OnProgress(key, bytes),
            (validator, token) => KeepValidatorAsync(run, validator, token), run.Cancel.Token).ConfigureAwait(false);
        await OnOutcomeAsync(run, download, from, outcome).ConfigureAwait(false);
    }

    // The server hears "downloading" before the file is asked for, so "completed" always comes after it. The transfer sends it
    // itself; when a sender held the place first, it waits for that one, which may have looked before "downloading" was
    // written, and then sends what is left. All of it takes at most ReportBudget, and ends at once when the transfer stops (a
    // pause, a replace, a delete). One that did not go waits out its delay, and the server work sends it then: the loop is
    // woken to know when.
    private async Task SendStartReportAsync(Running run, TaskCompletionSource? sender)
    {
        var key = run.Key;
        using var budget = new CancellationTokenSource(ReportBudget, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopToken, budget.Token, run.Cancel.Token);
        try
        {
            while (sender is null)
            {
                await SenderOf(key).WaitAsync(linked.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (linked.IsCancellationRequested || !HasReportDue(key))
                {
                    return;
                }
                sender = TryBeginSending(key);
            }
            await SendReportsAsSenderAsync(run.Server, key, sender, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            LeaveReportForLater(key);
        }
    }

    // A report is there and does not wait out its delay after one that could not go.
    private bool HasReportDue(Key key)
    {
        if (_store.Find(key.Scope, key.Id)?.Record.PendingReport is null)
        {
            return false;
        }
        lock (_gate)
        {
            return IsReportDueLocked(key, _time.GetUtcNow(), sending: true);
        }
    }

    // A report still to send after the transfer's own try: it waits out a delay (the first one when it ran out of time rather
    // than failed), and the loop looks again, so the server work sends it while the transfer runs.
    private void LeaveReportForLater(Key key)
    {
        if (_store.Find(key.Scope, key.Id)?.Record.PendingReport is null)
        {
            return;
        }
        lock (_gate)
        {
            if (!_reporting.ContainsKey(key) && !_reportRetries.ContainsKey(key))
            {
                _reportRetries[key] = new Retry(_time.GetUtcNow() + RetryDelays[0], 1);
            }
            WakeLocked();
        }
    }

    private static TransferJob JobOf(Running run, StoredDownload download, long from) =>
        new(run.Server, run.Key.Scope, run.Key.Id, download.Folder, download.Record.ExpectedBytes, from, download.Record.Validator);

    // Before its first transfer a download gets its manifest, which says the file's exact length, and its artwork and subtitles.
    // A manifest that does not come ends the attempt as the file's request would have.
    private async Task<(StoredDownload Download, TransferOutcome? Failed)> FetchAssetsFirstAsync(Running run, StoredDownload download)
    {
        var key = run.Key;
        if (download.Record.ManifestReadAt is not null && File.Exists(Path.Combine(download.Folder, DownloadPaths.Manifest)))
        {
            return (download, null);
        }
        try
        {
            var fetched = await _assets.FetchAsync(run.Server, download, run.Cancel.Token).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            long expected = fetched.Manifest.Integrity is { ExpectedBytes: > 0 } integrity ? integrity.ExpectedBytes : download.Record.ExpectedBytes;
            var updated = await _store.UpdateAsync(key.Scope, key.Id, r => r.Revision != run.Revision ? r : r with
            {
                ExpectedBytes = expected,
                ManifestReadAt = now,
                DurationMs = DownloadRecords.DurationOf(fetched.Manifest) ?? r.DurationMs,
                ArtworkFiles = fetched.ArtworkFiles,
                SubtitleRevisions = fetched.SubtitleRevisions,
            }, run.Cancel.Token).ConfigureAwait(false);
            return (updated, null);
        }
        catch (KeyNotFoundException)
        {
            // Deleted meanwhile: the transfer ends as a stopped one.
            lock (_gate)
            {
                CancelLocked(run);
            }
            throw new OperationCanceledException("The download was deleted.", run.Cancel.Token);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && run.Cancel.IsCancellationRequested))
        {
            var (result, retryAfter) = ResultOf(ex);
            _logger.LogInformation("The manifest of download {DownloadId} did not come ({Failure}): {Result}", key.Id,
                ex is SiloApiException api ? api.Status.ToString(System.Globalization.CultureInfo.InvariantCulture) : DownloadLog.Failure(ex), result);
            return (download, new TransferOutcome(result, download.Record.Validator, retryAfter, PartLength(download)));
        }
    }

    // A request that did not get the download's bytes, as the transfer would have taken its answer.
    private static (TransferResult Result, TimeSpan? RetryAfter) ResultOf(Exception ex) => ex switch
    {
        ProfileChangedException => (TransferResult.ProfileChanged, null),
        SiloApiException { Status: 401 } => (TransferResult.SignedOut, null),
        SiloApiException { Status: 403, Code: ProblemCodes.ProfileVerificationRequired } => (TransferResult.SignedOut, null),
        SiloApiException { Status: 404 } => (TransferResult.Gone, null),
        SiloApiException { Status: 429 or 503 } busy => (TransferResult.Busy, busy.RetryAfter),
        SiloApiException { Status: >= 500 } => (TransferResult.Unreachable, null),
        SiloApiException or JsonException => (TransferResult.Refused, null),
        DirectoryNotFoundException => (TransferResult.FolderMissing, null),
        IOException { HResult: DiskFull or HandleDiskFull } => (TransferResult.NotEnoughSpace, null),
        _ => (TransferResult.Unreachable, null),
    };

    // Windows' errors for a full drive.
    private const int DiskFull = unchecked((int)0x80070070);
    private const int HandleDiskFull = unchecked((int)0x80070027);

    // A transfer stopped (its download deleted, paused or out of scope, a metered link) waits as queued, its bytes kept. One the
    // queue's own stop ended stays Downloading: the next start resumes it with the margin.
    private async Task AfterFailureAsync(Running run, Exception ex)
    {
        try
        {
            if (ex is OperationCanceledException && run.Cancel.IsCancellationRequested)
            {
                bool stopping;
                lock (_gate)
                {
                    // Set before the queue's stop cancels anything.
                    stopping = _stopping;
                }
                if (!stopping)
                {
                    await ChangeOwnAsync(run.Key, run.Revision, r => r with { State = DownloadState.Queued }).ConfigureAwait(false);
                }
                return;
            }
            _logger.LogWarning("Download {DownloadId} stopped unexpectedly ({Failure}); it is tried again", run.Key.Id, DownloadLog.Failure(ex));
            lock (_gate)
            {
                RetryLaterLocked(run.Key, progressed: false);
            }
            await ChangeOwnAsync(run.Key, run.Revision, r => r with { State = DownloadState.Queued }).ConfigureAwait(false);
        }
        catch (Exception again)
        {
            _logger.LogWarning("Download {DownloadId} could not be set back to queued ({Failure})", run.Key.Id, DownloadLog.Failure(again));
        }
    }

    // The record turns Downloading, with a status report for the server when it was not before; null when the download went, or
    // is no longer to run, since the loop chose it. A new report does not wait out the delay of the one it replaces.
    private async Task<StoredDownload?> MarkDownloadingAsync(Running run)
    {
        var key = run.Key;
        var now = _time.GetUtcNow();
        bool start = false;
        bool reported = false;
        try
        {
            var download = await _store.UpdateAsync(key.Scope, key.Id, r =>
            {
                start = r.Revision == run.Revision
                    && r is { State: DownloadState.Queued or DownloadState.Downloading, IsPausedByViewer: false, IsNoLongerAvailable: false };
                reported = start && r.State != DownloadState.Downloading;
                return !start ? r
                    : !reported ? r with { Failure = DownloadFailure.None }
                    : r with
                    {
                        State = DownloadState.Downloading,
                        Failure = DownloadFailure.None,
                        PendingReport = new DownloadStatusEvent(DownloadStatuses.Downloading, now, r.Revision),
                        NextReport = null,
                    };
            }, run.Cancel.Token).ConfigureAwait(false);
            if (reported)
            {
                lock (_gate)
                {
                    _reportRetries.Remove(key);
                }
            }
            return start && !download.FilesMissing ? download : null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    // Where the bytes on disk carry on from: the whole part, or less the margin for a transfer a run before this one left.
    private long ResumePoint(StoredDownload download)
    {
        long length = PartLength(download);
        bool margin;
        lock (_gate)
        {
            margin = _resumeWithMargin.Remove(Key.Of(download.Record));
        }
        return margin ? Math.Max(0, length - ResumeMargin) : length;
    }

    // The validator is on the disk before the transfer relies on the bytes it belongs to, so a stop or a kill resumes with it.
    private async Task KeepValidatorAsync(Running run, string? validator, CancellationToken cancellationToken)
    {
        try
        {
            await _store.UpdateAsync(run.Key.Scope, run.Key.Id, r => r with { Validator = validator }, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            // Deleted meanwhile: the transfer ends as a stopped one.
            lock (_gate)
            {
                CancelLocked(run);
            }
            throw new OperationCanceledException("The download was deleted.", run.Cancel.Token);
        }
    }

    private void OnProgress(Key key, long bytes)
    {
        var now = _time.GetUtcNow();
        bool say;
        lock (_gate)
        {
            _bytes[key] = bytes;
            if (!_rates.TryGetValue(key, out var rate))
            {
                rate = new TransferRate();
                _rates[key] = rate;
            }
            rate.Add(now, bytes);
            say = !_progressSaid.TryGetValue(key, out var said) || now - said >= ProgressInterval;
            if (say)
            {
                _progressSaid[key] = now;
            }
        }
        if (say)
        {
            SayProgress(key.Id);
        }
    }

    // A follower that throws does not stop the transfer.
    private void SayProgress(string downloadId)
    {
        try
        {
            Progressed?.Invoke(this, downloadId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("A follower of the downloads' progress failed ({Failure})", DownloadLog.Failure(ex));
        }
    }

    private async Task OnOutcomeAsync(Running run, StoredDownload download, long from, TransferOutcome outcome)
    {
        var key = run.Key;
        bool failsTwice = false;
        lock (_gate)
        {
            _bytes[key] = outcome.Bytes;
            if (outcome.Result == TransferResult.Integrity)
            {
                failsTwice = !_integrityFailed.Add(key);
            }
            else
            {
                _integrityFailed.Remove(key);
            }
        }
        switch (outcome.Result)
        {
            case TransferResult.Completed:
                await CompleteAsync(run, download, outcome).ConfigureAwait(false);
                return;
            case TransferResult.NotEnoughSpace:
                await ChangeOwnAsync(key, run.Revision, r => r with
                {
                    State = DownloadState.Failed,
                    Failure = DownloadFailure.NotEnoughSpace,
                    Validator = outcome.Validator,
                }).ConfigureAwait(false);
                return;
            case TransferResult.Integrity:
                await OnIntegrityAsync(run, failsTwice, outcome).ConfigureAwait(false);
                return;
            case TransferResult.Expired or TransferResult.Refused or TransferResult.Gone:
                // The transfer's own token: a pause, a replace or a stop ends a prepare request under way at once.
                await OnServerRefusalAsync(run, download, outcome, run.Cancel.Token).ConfigureAwait(false);
                return;
            case TransferResult.Busy:
                lock (_gate)
                {
                    int failures = _retries.GetValueOrDefault(key).Failures;
                    _retries[key] = new Retry(_time.GetUtcNow() + BusyWait(outcome.RetryAfter, failures), failures + 1);
                }
                break;
            case TransferResult.SignedOut or TransferResult.ProfileChanged:
                _logger.LogInformation("The downloads of this profile wait for its sign-in ({Result})", outcome.Result);
                lock (_gate)
                {
                    // The generation the transfer started in: a session that changed meanwhile has the profile run again.
                    _stoppedScopes[key.Scope] = run.Generation;
                }
                break;
            default:
                // Unreachable or a missing folder: tried again later, soon again when the transfer got further. A folder found
                // gone is looked at again at the next pass.
                lock (_gate)
                {
                    RetryLaterLocked(key, progressed: outcome.Bytes > from);
                    _recheckRequested |= outcome.Result == TransferResult.FolderMissing;
                }
                break;
        }
        await ChangeOwnAsync(key, run.Revision, r => r with { State = DownloadState.Queued, Validator = outcome.Validator }).ConfigureAwait(false);
    }

    // A file of another length starts over once, from its first byte; the second time in a row it fails.
    private async Task OnIntegrityAsync(Running run, bool failsTwice, TransferOutcome outcome)
    {
        if (failsTwice)
        {
            _logger.LogWarning("Download {DownloadId} arrived wrong twice; it fails", run.Key.Id);
            lock (_gate)
            {
                _integrityFailed.Remove(run.Key);
            }
            await ChangeOwnAsync(run.Key, run.Revision, r => r with
            {
                State = DownloadState.Failed,
                Failure = DownloadFailure.Integrity,
                Validator = outcome.Validator,
            }).ConfigureAwait(false);
            return;
        }
        await ChangeOwnAsync(run.Key, run.Revision, r => r with { State = DownloadState.Queued, Validator = null }).ConfigureAwait(false);
    }

    // The whole file gets its media name, then the record says so and the server hears it. A part that cannot be renamed yet
    // is tried again later: the next transfer finds it whole.
    private async Task CompleteAsync(Running run, StoredDownload download, TransferOutcome outcome)
    {
        var key = run.Key;
        string name = DownloadPaths.MediaName(ContainerOf(download));
        try
        {
            File.Move(download.PartPath, Path.Combine(download.Folder, name), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The finished file of download {DownloadId} could not be named ({Failure}); it is tried again", key.Id, DownloadLog.Failure(ex));
            lock (_gate)
            {
                RetryLaterLocked(key, progressed: false);
            }
            await ChangeOwnAsync(key, run.Revision, r => r with { State = DownloadState.Queued, Validator = outcome.Validator }).ConfigureAwait(false);
            return;
        }
        var now = _time.GetUtcNow();
        await ChangeOwnAsync(key, run.Revision, r =>
        {
            var completed = r with { State = DownloadState.Completed, Failure = DownloadFailure.None, MediaFile = name, PreparationProgress = null };
            var report = new DownloadStatusEvent(DownloadStatuses.Completed, now, r.Revision);
            // A "downloading" the server has not heard goes first; "completed" follows it.
            return r.PendingReport is { Status: DownloadStatuses.Downloading }
                ? completed with { NextReport = report }
                : completed with { PendingReport = report, NextReport = null };
        }).ConfigureAwait(false);
        lock (_gate)
        {
            _retries.Remove(key);
        }
        // The server work sends "completed": the transfer's slot is free for the next one meanwhile.
        _logger.LogInformation("Download {DownloadId} is on this PC", key.Id);
    }

    // The container the finished file is named after, from the manifest kept beside it.
    private string? ContainerOf(StoredDownload download)
    {
        try
        {
            return OfflineManifest.Read(File.ReadAllBytes(Path.Combine(download.Folder, DownloadPaths.Manifest))).Container;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogInformation("The manifest of download {DownloadId} could not be read ({Failure}); its file is named without its container",
                download.Record.DownloadId, DownloadLog.Failure(ex));
            return null;
        }
    }

    // Changes the record a transfer ran for, while it is still that transfer's: Downloading, of the same revision. Anything
    // else changed it meanwhile and stays; a download that went is no change.
    private async Task ChangeOwnAsync(Key key, long revision, Func<DownloadRecord, DownloadRecord> change, CancellationToken cancellationToken = default)
    {
        try
        {
            await _store.UpdateAsync(key.Scope, key.Id, r => r.State == DownloadState.Downloading && r.Revision == revision ? change(r) : r,
                cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            // Deleted meanwhile.
        }
    }

    private void RetryLaterLocked(Key key, bool progressed)
    {
        int failures = progressed ? 0 : _retries.GetValueOrDefault(key).Failures;
        _retries[key] = new Retry(_time.GetUtcNow() + RetryDelays[Math.Min(failures, RetryDelays.Count - 1)], failures + 1);
    }

    // A busy server's wait: its Retry-After from a second up to MaxBusyWait (hours are five minutes); without one, or with less
    // than a second (a date already past, 0), the retry delays, longer with each busy answer in a row.
    private static TimeSpan BusyWait(TimeSpan? retryAfter, int failures) => retryAfter switch
    {
        { } wait when wait >= RetryDelays[0] => wait > MaxBusyWait ? MaxBusyWait : wait,
        _ => RetryDelays[Math.Min(failures, RetryDelays.Count - 1)],
    };

    // The scope's reports, but none that waits out its delay after one that could not go.
    private async Task SendPendingReportsAsync(DownloadScope scope, Uri server, CancellationToken stop)
    {
        foreach (var download in _store.In(scope).Where(d => d.Record.PendingReport is not null))
        {
            if (!IsStillActive(scope))
            {
                return;
            }
            var key = Key.Of(download.Record);
            bool due;
            lock (_gate)
            {
                due = IsReportDueLocked(key, _time.GetUtcNow());
            }
            if (due)
            {
                await SendReportsAsync(server, key, stop).ConfigureAwait(false);
            }
        }
    }

    // Sends a download's status reports, the next one after the pending one, until none is left or one could not go. One sender
    // per download at a time: a second caller has the one under way look once more when it is done (SenderOf tells when), but a
    // report that could not go waits out its delay. Never throws.
    private async Task SendReportsAsync(Uri server, Key key, CancellationToken stop)
    {
        if (TryBeginSending(key) is { } done)
        {
            await SendReportsAsSenderAsync(server, key, done, stop).ConfigureAwait(false);
        }
    }

    // The sender's place for a download's reports; null when a sender is under way, which then looks once more when it is done.
    private TaskCompletionSource? TryBeginSending(Key key)
    {
        lock (_gate)
        {
            if (_reporting.ContainsKey(key))
            {
                _reportAgain.Add(key);
                return null;
            }
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _reporting[key] = done;
            return done;
        }
    }

    // A sender's place given up before it sent anything.
    private void EndSending(Key key, TaskCompletionSource done)
    {
        lock (_gate)
        {
            EndSendingLocked(key);
        }
        done.TrySetResult();
    }

    private async Task SendReportsAsSenderAsync(Uri server, Key key, TaskCompletionSource done, CancellationToken stop)
    {
        try
        {
            DownloadStatusEvent? kept = null;
            while (true)
            {
                var report = _store.Find(key.Scope, key.Id)?.Record.PendingReport;
                if (report is null || report == kept)
                {
                    lock (_gate)
                    {
                        if (!_reportAgain.Remove(key) || (report is not null && !IsReportDueLocked(key, _time.GetUtcNow(), sending: true)))
                        {
                            EndSendingLocked(key);
                            return;
                        }
                    }
                    kept = null;
                    continue;
                }
                lock (_gate)
                {
                    // One that could not go a moment ago waits out its delay, whoever asks to send it.
                    if (!IsReportDueLocked(key, _time.GetUtcNow(), sending: true))
                    {
                        EndSendingLocked(key);
                        return;
                    }
                }
                if (!await SendReportAsync(server, key, report, stop).ConfigureAwait(false))
                {
                    kept = report;
                }
            }
        }
        catch (Exception ex)
        {
            bool stopped = ex is OperationCanceledException && stop.IsCancellationRequested;
            lock (_gate)
            {
                EndSendingLocked(key);
                if (!stopped)
                {
                    // An answer no one expected waits the delays as one that did not come: the server work does not come back
                    // for it at once.
                    RetryReportLaterLocked(key);
                }
            }
            if (!stopped)
            {
                _logger.LogWarning("The status of download {DownloadId} was not sent ({Failure})", key.Id, DownloadLog.Failure(ex));
            }
        }
        finally
        {
            done.TrySetResult();
        }
    }

    private void EndSendingLocked(Key key)
    {
        _reporting.Remove(key);
        _reportAgain.Remove(key);
    }

    // The end of the send under way for a download's reports, or done when none is.
    private Task SenderOf(Key key)
    {
        lock (_gate)
        {
            return _reporting.TryGetValue(key, out var sender) ? sender.Task : Task.CompletedTask;
        }
    }

    // One status report: true once the server took it or never will; false when it is to be sent again, as it is.
    private async Task<bool> SendReportAsync(Uri server, Key key, DownloadStatusEvent report, CancellationToken stop)
    {
        try
        {
            await _api.ReportAsync(server, key.Scope.ProfileId, key.Id, report, stop).ConfigureAwait(false);
        }
        catch (SiloApiException ex) when (IsFinal(ex))
        {
            // Another revision (409), an entry preparing again (404) or a time the server refuses: sending it again changes
            // nothing.
            _logger.LogInformation("The server did not take the status of download {DownloadId} ({Status}); it is not sent again", key.Id, ex.Status);
        }
        catch (Exception ex) when (ex is HttpRequestException or SiloApiException or ProfileChangedException or JsonException
            || (ex is OperationCanceledException && !stop.IsCancellationRequested))
        {
            lock (_gate)
            {
                RetryReportLaterLocked(key);
            }
            _logger.LogInformation("The status of download {DownloadId} could not be sent ({Failure}); it is sent again",
                key.Id, ex is SiloApiException api ? api.Status.ToString(System.Globalization.CultureInfo.InvariantCulture) : DownloadLog.Failure(ex));
            return false;
        }
        lock (_gate)
        {
            _reportRetries.Remove(key);
        }
        try
        {
            // The next report, as "completed" behind a "downloading", moves up.
            await _store.UpdateAsync(key.Scope, key.Id, r => r.PendingReport == report ? r with { PendingReport = r.NextReport, NextReport = null } : r,
                stop).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            // Deleted meanwhile.
        }
        return true;
    }

    private void RetryReportLaterLocked(Key key)
    {
        int failures = _reportRetries.GetValueOrDefault(key).Failures;
        _reportRetries[key] = new Retry(_time.GetUtcNow() + RetryDelays[Math.Min(failures, RetryDelays.Count - 1)], failures + 1);
    }

    // A client error the same report gets again; not a sign-in, a timeout or a busy server.
    private static bool IsFinal(SiloApiException ex) =>
        ex.Status is >= 400 and < 500 and not (401 or 408 or 429)
        && !(ex.Status == 403 && ex.Code == ProblemCodes.ProfileVerificationRequired);
}
