using System.Net;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>Making, changing and stopping a monitor, as the download dialog's Monitor asks.</summary>
public sealed partial class SeriesMonitors
{
    /// <summary>
    /// Monitors <paramref name="seriesId"/> as <paramref name="choice"/> says, then syncs it and takes what it registered. The
    /// create is sent once: after no clear answer the list tells whether the server made it. When this PC monitors the series
    /// already, the server answers with that monitor as it is, and what differs is sent as a change.
    /// </summary>
    public async Task<MonitorResult> CreateAsync(string seriesId, string seriesTitle, MonitorChoice choice, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(seriesId);
        ArgumentNullException.ThrowIfNull(choice);
        return await WriteAsync(async (scope, server, token) =>
        {
            _book.NoteTitle(scope, seriesId, seriesTitle);
            var body = new CreateSubscriptionBody
            {
                SeriesId = seriesId,
                Mode = choice.Mode,
                DeleteWatched = choice.DeleteWatched,
                MaxStorageBytes = Math.Max(0, choice.MaxStorageBytes),
                Quality = choice.Quality,
            };
            DownloadSubscription monitor;
            try
            {
                monitor = await _api.CreateAsync(server, scope.ProfileId, body, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsUncertain(ex, token))
            {
                _logger.LogInformation("A monitor's create got no clear answer ({Failure}); the list is read", Describe(ex));
                if (!await ReadListAsync(scope, server, token, monitoring: seriesId).ConfigureAwait(false) || _book.ForSeries(scope, seriesId) is not { } listed)
                {
                    return MonitorResult.Uncertain;
                }
                monitor = listed;
            }
            catch (Exception ex) when (IsServerFailure(ex, token))
            {
                _logger.LogInformation("A monitor could not be created ({Failure})", Describe(ex));
                return MonitorResult.Failed;
            }
            if (!IsStill(scope))
            {
                return MonitorResult.Unavailable;
            }
            // A stop of this monitor that waited is no longer wanted: the series is monitored again.
            ForgetStop(scope, monitor.Id);
            _book.Put(scope, monitor);
            if (!Matches(monitor, choice))
            {
                // The server's monitor as it was (another client's, or one this PC stopped and monitors again) is not what the
                // viewer asked for: until it is, its Delete after watching is not taken at its word.
                Unconfirm(scope, monitor.Id, new UnconfirmedChoice(choice, monitor.DeleteWatched));
                var edit = new MonitorEdit(monitor.Mode == choice.Mode ? null : choice.Mode, choice.DeleteWatched, choice.MaxStorageBytes,
                    choice.Quality is null || choice.Quality == (monitor.Quality ?? DownloadQualities.Original) ? null : choice.Quality);
                var changed = await ChangeAsync(scope, server, monitor, edit, token).ConfigureAwait(false);
                if (changed == MonitorResult.Saved)
                {
                    Confirm(scope, monitor.Id);
                }
                return changed;
            }
            await SyncAndTakeAsync(scope, server, monitor, token).ConfigureAwait(false);
            return MonitorResult.Saved;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Changes a monitor under the validator it was read with, then syncs it and takes what it registered. One that changed on the
    /// server since is read again (<see cref="MonitorResult.Changed"/>) and nothing more is sent: the viewer saves again over what
    /// they now see. One the server no longer has leaves the list (<see cref="MonitorResult.Removed"/>).
    /// </summary>
    public async Task<MonitorResult> UpdateAsync(string subscriptionId, MonitorEdit edit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionId);
        ArgumentNullException.ThrowIfNull(edit);
        return await WriteAsync(async (scope, server, token) => _book.Find(scope, subscriptionId) switch
        {
            null => MonitorResult.Removed,
            // The viewer changed the monitor as it was, which a run read anew since: their change goes over nothing they saw.
            { } monitor when edit.Etag is { } seen && monitor.Etag != seen => MonitorResult.Changed,
            { } monitor => await SavedChangeAsync(scope, server, monitor, edit, token).ConfigureAwait(false),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops monitoring: the monitor leaves the list at once, and its delete goes under its validator, now or, while the server
    /// cannot be reached, with a later run, also after Granary starts again (<see cref="MonitorPendingFile"/>); the list does not
    /// bring it back meanwhile. The downloads it registered stay.
    /// </summary>
    public async Task<MonitorResult> StopAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionId);
        return await WriteAsync(async (scope, server, token) =>
        {
            if (_book.Find(scope, subscriptionId) is not { } monitor)
            {
                return MonitorResult.Saved;
            }
            lock (_gate)
            {
                if (!_pendingStops.TryGetValue(scope, out var stops))
                {
                    _pendingStops[scope] = stops = new Dictionary<string, string>(StringComparer.Ordinal);
                }
                stops[monitor.Id] = monitor.Etag;
            }
            SavePending();
            _book.Remove(scope, monitor.Id);
            Confirm(scope, monitor.Id);
            await SendStopsAsync(scope, server, token).ConfigureAwait(false);
            return MonitorResult.Saved;
        }, cancellationToken).ConfigureAwait(false);
    }

    // One write at a time with the runs, for the scope it began in. The caller's token cancels the wait for a run under way
    // (OperationCanceledException), never the write once it began: a create the server may have made is not cut off.
    private async Task<MonitorResult> WriteAsync(Func<DownloadScope, Uri, CancellationToken, Task<MonitorResult>> write, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        if (Target() is not var (scope, server) || _store.IsClearPending(scope))
        {
            return MonitorResult.Unavailable;
        }
        await _one.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!await LoadPendingAsync().ConfigureAwait(false))
            {
                return MonitorResult.Failed;
            }
            return IsStill(scope) ? await write(scope, server, _stop.Token).ConfigureAwait(false) : MonitorResult.Unavailable;
        }
        finally
        {
            _one.Release();
        }
    }

    // A viewer's change that went: what they chose is the monitor's now, so a create's choice still waiting for it is done.
    private async Task<MonitorResult> SavedChangeAsync(DownloadScope scope, Uri server, DownloadSubscription monitor, MonitorEdit edit,
        CancellationToken cancellationToken)
    {
        var result = await ChangeAsync(scope, server, monitor, edit, cancellationToken).ConfigureAwait(false);
        if (result == MonitorResult.Saved)
        {
            Confirm(scope, monitor.Id);
        }
        return result;
    }

    private void Unconfirm(DownloadScope scope, string subscriptionId, UnconfirmedChoice choice)
    {
        lock (_gate)
        {
            if (!_unconfirmed.TryGetValue(scope, out var choices))
            {
                _unconfirmed[scope] = choices = new Dictionary<string, UnconfirmedChoice>(StringComparer.Ordinal);
            }
            choices[subscriptionId] = choice;
        }
        SavePending();
    }

    private void Confirm(DownloadScope scope, string subscriptionId)
    {
        bool confirmed;
        lock (_gate)
        {
            confirmed = _unconfirmed.TryGetValue(scope, out var choices) && choices.Remove(subscriptionId);
        }
        if (confirmed)
        {
            SavePending();
        }
    }

    // The monitors whose create's choice the server does not show yet.
    private HashSet<string> UnconfirmedOf(DownloadScope scope)
    {
        lock (_gate)
        {
            return _unconfirmed.TryGetValue(scope, out var choices) ? [.. choices.Keys] : [];
        }
    }

    // A whole list read: a monitor it shows as its create asked, or does not show at all, waits for nothing more; nor does one
    // whose Delete after watching changed since the viewer's choice, a newer word on it. Any other change (the server pausing
    // it when its storage is revoked) says nothing of that setting, so the choice still waits.
    private void ConfirmListed(DownloadScope scope, IReadOnlyList<DownloadSubscription> listed)
    {
        bool confirmed = false;
        lock (_gate)
        {
            if (!_unconfirmed.TryGetValue(scope, out var choices))
            {
                return;
            }
            foreach (var (id, waiting) in choices.ToList())
            {
                if (listed.FirstOrDefault(m => m.Id == id) is not { } monitor || Matches(monitor, waiting.Choice) || monitor.DeleteWatched != waiting.ServerDeleteWatched)
                {
                    confirmed |= choices.Remove(id);
                }
            }
        }
        if (confirmed)
        {
            SavePending();
        }
    }

    private async Task<MonitorResult> ChangeAsync(DownloadScope scope, Uri server, DownloadSubscription monitor, MonitorEdit edit, CancellationToken cancellationToken)
    {
        var body = new UpdateSubscriptionBody
        {
            Mode = edit.Mode,
            DeleteWatched = edit.DeleteWatched,
            MaxStorageBytes = Math.Max(0, edit.MaxStorageBytes),
            Active = true,
            Quality = edit.Quality,
        };
        DownloadSubscription changed;
        try
        {
            changed = await _api.UpdateAsync(server, scope.ProfileId, monitor.Id, monitor.Etag, body, cancellationToken).ConfigureAwait(false);
        }
        catch (SiloApiException ex) when (ex.Status is (int)HttpStatusCode.PreconditionFailed or (int)HttpStatusCode.Conflict)
        {
            _logger.LogInformation("A monitor changed on the server since it was read ({Status}); it is read again", ex.Status);
            var reread = await ReadAgainAsync(scope, server, monitor.Id, cancellationToken).ConfigureAwait(false);
            // Changed only once what changed was read: a read that failed leaves the old monitor, which is not what changed.
            return reread is not null ? MonitorResult.Changed
                : _book.Find(scope, monitor.Id) is null ? MonitorResult.Removed
                : MonitorResult.Uncertain;
        }
        catch (SiloApiException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            _book.Remove(scope, monitor.Id);
            return MonitorResult.Removed;
        }
        catch (Exception ex) when (IsServerFailure(ex, cancellationToken))
        {
            _logger.LogInformation("A monitor could not be changed ({Failure})", Describe(ex));
            return ex is SiloApiException { Status: < 500 } ? MonitorResult.Failed : MonitorResult.Uncertain;
        }
        if (!IsStill(scope))
        {
            return MonitorResult.Unavailable;
        }
        _book.Put(scope, changed);
        await SyncAndTakeAsync(scope, server, changed, cancellationToken).ConfigureAwait(false);
        return MonitorResult.Saved;
    }

    // After a create or a change: the server registers what the monitor takes now, and the PC takes it.
    private async Task SyncAndTakeAsync(DownloadScope scope, Uri server, DownloadSubscription monitor, CancellationToken cancellationToken)
    {
        if (monitor.Active)
        {
            // Nothing is deleted after a write's sync: what it could not be sure of stays for the next run to read.
            await SyncOneAsync(scope, server, monitor, new HashSet<string>(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
        }
        if (IsStill(scope))
        {
            await TakeRegisteredAsync(scope, server, cancellationToken).ConfigureAwait(false);
        }
    }

    // The stops still to go for the scope, each under the validator its monitor was last read with. One whose monitor changed
    // since (412, or 409 while the server applied another change) goes once more under its validator read again, as the Apple
    // app's: a stop does not depend on what changed. One that changed again by then waits for the next run, as one that got no
    // answer does. One the server will not take is dropped (the list shows the monitor again).
    private async Task SendStopsAsync(DownloadScope scope, Uri server, CancellationToken cancellationToken)
    {
        foreach (var (id, etag) in StopsOf(scope))
        {
            if (!IsStill(scope))
            {
                return;
            }
            try
            {
                try
                {
                    await _api.DeleteAsync(server, scope.ProfileId, id, etag, cancellationToken).ConfigureAwait(false);
                }
                catch (SiloApiException ex) when (ex.Status is (int)HttpStatusCode.PreconditionFailed or (int)HttpStatusCode.Conflict)
                {
                    var current = await _api.GetAsync(server, scope.ProfileId, id, cancellationToken).ConfigureAwait(false);
                    await _api.DeleteAsync(server, scope.ProfileId, id, current.Etag, cancellationToken).ConfigureAwait(false);
                }
                ForgetStop(scope, id);
            }
            catch (SiloApiException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
            {
                ForgetStop(scope, id);
            }
            catch (SiloApiException ex) when (ex.Status is (int)HttpStatusCode.PreconditionFailed or (int)HttpStatusCode.Conflict)
            {
                // It changed again between the read and the second try: the server still has it, and the stop is still wanted.
                // It goes with the next run, as the Apple app keeps a stop that met a conflict; the other stops go now.
                _logger.LogInformation("A monitor changed again under its stop ({Status}); the stop goes with the next run", ex.Status);
            }
            catch (SiloApiException ex) when (ex.Status is >= 400 and < 500 and not (int)HttpStatusCode.RequestTimeout and not (int)HttpStatusCode.TooManyRequests)
            {
                _logger.LogInformation("The server did not take a monitor's stop ({Status}); it is not sent again", ex.Status);
                ForgetStop(scope, id);
            }
            catch (Exception ex) when (IsServerFailure(ex, cancellationToken))
            {
                _logger.LogInformation("A monitor's stop could not be sent ({Failure}); it goes with the next run", Describe(ex));
                return;
            }
        }
    }

    private Dictionary<string, string> StopsOf(DownloadScope scope)
    {
        lock (_gate)
        {
            return _pendingStops.TryGetValue(scope, out var stops) ? new Dictionary<string, string>(stops, StringComparer.Ordinal) : [];
        }
    }

    private void ForgetStop(DownloadScope scope, string subscriptionId)
    {
        bool forgot;
        lock (_gate)
        {
            forgot = _pendingStops.TryGetValue(scope, out var stops) && stops.Remove(subscriptionId);
        }
        if (forgot)
        {
            SavePending();
        }
    }

    /// <summary>For tests: the latest write of the writes still to send; done once it is on disk, or failed and logged.</summary>
    internal Task PendingSaved
    {
        get
        {
            lock (_gate)
            {
                return _pendingSave;
            }
        }
    }

    // Under _one: the stops and the choices a run before this one could not send, read off the caller's thread, once. False
    // while the file cannot be read now: it is read again with the next run or write, and until then no run or write goes, as
    // a run would delete on a monitor's old word or bring back a stopped one, and a write would be undone by what it holds.
    private async Task<bool> LoadPendingAsync()
    {
        if (_pendingLoaded || _pendingFile is not { } file)
        {
            return true;
        }
        if (await Task.Run(file.Load).ConfigureAwait(false) is not { } kept)
        {
            return false;
        }
        lock (_gate)
        {
            _pendingLoaded = true;
            foreach (var stop in kept.Stops)
            {
                if (!_pendingStops.TryGetValue(stop.Scope, out var stops))
                {
                    _pendingStops[stop.Scope] = stops = new Dictionary<string, string>(StringComparer.Ordinal);
                }
                stops.TryAdd(stop.SubscriptionId, stop.Etag);
            }
            foreach (var choice in kept.Choices)
            {
                if (!_unconfirmed.TryGetValue(choice.Scope, out var choices))
                {
                    _unconfirmed[choice.Scope] = choices = new Dictionary<string, UnconfirmedChoice>(StringComparer.Ordinal);
                }
                choices.TryAdd(choice.SubscriptionId, choice.Choice);
            }
        }
        return true;
    }

    // Every scope's stops and choices as they are now, written one save after another on the thread pool, so the last written is
    // the latest; it never throws, so the chain never breaks. Not before the file was read: what it holds is not written over.
    private void SavePending()
    {
        if (_pendingFile is not { } file)
        {
            return;
        }
        lock (_gate)
        {
            _pendingSave = _pendingSave.ContinueWith(_ =>
            {
                MonitorPending pending;
                lock (_gate)
                {
                    if (!_pendingLoaded)
                    {
                        return;
                    }
                    pending = new MonitorPending(
                        [.. _pendingStops.SelectMany(scope => scope.Value.Select(stop => new PendingStop(scope.Key, stop.Key, stop.Value)))],
                        [.. _unconfirmed.SelectMany(scope => scope.Value.Select(choice => new PendingChoice(scope.Key, choice.Key, choice.Value)))]);
                }
                file.Save(pending);
            }, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
        }
    }

    // The monitor already takes what the choice asks for, running.
    private static bool Matches(DownloadSubscription monitor, MonitorChoice choice) =>
        monitor.Active && monitor.Mode == choice.Mode && monitor.DeleteWatched == choice.DeleteWatched
        && monitor.MaxStorageBytes == Math.Max(0, choice.MaxStorageBytes)
        && (choice.Quality is null || choice.Quality == (monitor.Quality ?? DownloadQualities.Original));

    // No answer, a conflict or a server error: the server may have made it. A connection that was never made sent nothing.
    private static bool IsUncertain(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException { HttpRequestError: not (HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
            or HttpRequestError.SecureConnectionError) }
        || ex is SiloApiException { Status: (int)HttpStatusCode.Conflict or >= 500 }
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);
}
