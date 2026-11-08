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
                WakeLocked();
            }
            run.Cancel.Dispose();
        }
    }

    private async Task TransferAsync(Running run)
    {
        var key = run.Key;
        if (await MarkDownloadingAsync(run).ConfigureAwait(false) is not { } download)
        {
            return;
        }
        // The server hears of the transfer while it runs; the outcome waits for that report, so the next one follows it.
        var reports = SendReportsAsync(run.Server, key, _stop.Token);
        long from;
        TransferOutcome outcome;
        try
        {
            from = ResumePoint(download);
            var job = new TransferJob(run.Server, key.Scope, key.Id, download.Folder, download.Record.ExpectedBytes, from, download.Record.Validator,
                ViaProxy: false);
            outcome = await _transfer.RunAsync(job, bytes => OnProgress(key, bytes), (validator, token) => KeepValidatorAsync(run, validator, token),
                run.Cancel.Token).ConfigureAwait(false);
        }
        finally
        {
            await reports.ConfigureAwait(false);
        }
        await OnOutcomeAsync(run, download, from, outcome).ConfigureAwait(false);
    }

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
    // is no longer to run, since the loop chose it.
    private async Task<StoredDownload?> MarkDownloadingAsync(Running run)
    {
        var key = run.Key;
        var now = _time.GetUtcNow();
        bool start = false;
        try
        {
            var download = await _store.UpdateAsync(key.Scope, key.Id, r =>
            {
                start = r.Revision == run.Revision
                    && r is { State: DownloadState.Queued or DownloadState.Downloading, IsPausedByViewer: false, IsNoLongerAvailable: false };
                return !start ? r
                    : r.State == DownloadState.Downloading ? r with { Failure = DownloadFailure.None }
                    : r with
                    {
                        State = DownloadState.Downloading,
                        Failure = DownloadFailure.None,
                        PendingReport = new DownloadStatusEvent(DownloadStatuses.Downloading, now, r.Revision),
                    };
            }, run.Cancel.Token).ConfigureAwait(false);
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
                await OnServerRefusalAsync(download, outcome, _stop.Token).ConfigureAwait(false);
                return;
            case TransferResult.Busy:
                lock (_gate)
                {
                    _retries[key] = new Retry(_time.GetUtcNow() + BusyWait(outcome.RetryAfter), _retries.GetValueOrDefault(key).Failures);
                }
                break;
            case TransferResult.SignedOut or TransferResult.ProfileChanged:
                _logger.LogInformation("The downloads of this profile wait for its sign-in ({Result})", outcome.Result);
                lock (_gate)
                {
                    _stoppedScopes[key.Scope] = _sessionGeneration;
                }
                break;
            default:
                // Unreachable or a missing folder: tried again later, soon again when the transfer got further.
                lock (_gate)
                {
                    RetryLaterLocked(key, progressed: outcome.Bytes > from);
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
        await ChangeOwnAsync(key, run.Revision, r => r with
        {
            State = DownloadState.Completed,
            Failure = DownloadFailure.None,
            MediaFile = name,
            PreparationProgress = null,
            PendingReport = new DownloadStatusEvent(DownloadStatuses.Completed, now, r.Revision),
        }).ConfigureAwait(false);
        lock (_gate)
        {
            _retries.Remove(key);
        }
        _logger.LogInformation("Download {DownloadId} is on this PC", key.Id);
        await SendReportsAsync(run.Server, key, _stop.Token).ConfigureAwait(false);
    }

    // The container the finished file is named after.
    private static string? ContainerOf(StoredDownload download) => null;

    // Until the server's answers are read: a refusal fails the download.
    private Task OnServerRefusalAsync(StoredDownload download, TransferOutcome outcome, CancellationToken cancellationToken) =>
        ChangeOwnAsync(Key.Of(download.Record), download.Record.Revision, r => r with
        {
            State = DownloadState.Failed,
            Failure = DownloadFailure.Server,
            Validator = outcome.Validator,
        }, cancellationToken);

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

    // A busy server's wait, within 0 and MaxBusyWait: a date already past is now, hours are five minutes.
    private static TimeSpan BusyWait(TimeSpan? retryAfter) => retryAfter switch
    {
        null => RetryDelays[0],
        { } wait when wait < TimeSpan.Zero => TimeSpan.Zero,
        { } wait when wait > MaxBusyWait => MaxBusyWait,
        { } wait => wait,
    };

    private async Task SendPendingReportsAsync(DownloadScope scope, Uri server, CancellationToken stop)
    {
        foreach (var download in _store.In(scope).Where(d => d.Record.PendingReport is not null))
        {
            await SendReportsAsync(server, Key.Of(download.Record), stop).ConfigureAwait(false);
        }
    }

    // Sends a download's status reports until none is left or one could not go. One sender per download at a time: a second
    // caller has the one under way look once more when it is done. Never throws.
    private async Task SendReportsAsync(Uri server, Key key, CancellationToken stop)
    {
        lock (_gate)
        {
            if (!_reporting.Add(key))
            {
                _reportAgain.Add(key);
                return;
            }
        }
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
                        if (!_reportAgain.Remove(key))
                        {
                            _reporting.Remove(key);
                            return;
                        }
                    }
                    kept = null;
                    continue;
                }
                if (!await SendReportAsync(server, key, report, stop).ConfigureAwait(false))
                {
                    kept = report;
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _reporting.Remove(key);
                _reportAgain.Remove(key);
            }
            if (!(ex is OperationCanceledException && stop.IsCancellationRequested))
            {
                _logger.LogWarning("The status of download {DownloadId} was not sent ({Failure})", key.Id, DownloadLog.Failure(ex));
            }
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
            _logger.LogInformation("The status of download {DownloadId} could not be sent ({Failure}); it is sent again",
                key.Id, ex is SiloApiException api ? api.Status.ToString(System.Globalization.CultureInfo.InvariantCulture) : DownloadLog.Failure(ex));
            lock (_gate)
            {
                int failures = _reportRetries.GetValueOrDefault(key).Failures;
                _reportRetries[key] = new Retry(_time.GetUtcNow() + RetryDelays[Math.Min(failures, RetryDelays.Count - 1)], failures + 1);
            }
            return false;
        }
        lock (_gate)
        {
            _reportRetries.Remove(key);
        }
        try
        {
            await _store.UpdateAsync(key.Scope, key.Id, r => r.PendingReport == report ? r with { PendingReport = null } : r, stop).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            // Deleted meanwhile.
        }
        return true;
    }

    // A client error the same report gets again; not a sign-in, a timeout or a busy server.
    private static bool IsFinal(SiloApiException ex) =>
        ex.Status is >= 400 and < 500 and not (401 or 408 or 429)
        && !(ex.Status == 403 && ex.Code == ProblemCodes.ProfileVerificationRequired);
}
