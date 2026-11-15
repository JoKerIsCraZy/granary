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
                if (!await ReadListAsync(scope, server, token).ConfigureAwait(false) || _book.ForSeries(scope, seriesId) is not { } listed)
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
                var edit = new MonitorEdit(monitor.Mode == choice.Mode ? null : choice.Mode, choice.DeleteWatched, choice.MaxStorageBytes,
                    choice.Quality is null || choice.Quality == (monitor.Quality ?? DownloadQualities.Original) ? null : choice.Quality);
                return await ChangeAsync(scope, server, monitor, edit, token).ConfigureAwait(false);
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
        return await WriteAsync(async (scope, server, token) => _book.Find(scope, subscriptionId) is { } monitor
            ? await ChangeAsync(scope, server, monitor, edit, token).ConfigureAwait(false)
            : MonitorResult.Removed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops monitoring: the monitor leaves the list at once, and its delete goes under its validator, now or with the next run
    /// while the server cannot be reached; the list does not bring it back meanwhile. The downloads it registered stay.
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
            _book.Remove(scope, monitor.Id);
            await SendStopsAsync(scope, server, token).ConfigureAwait(false);
            return MonitorResult.Saved;
        }, cancellationToken).ConfigureAwait(false);
    }

    // One write at a time with the runs, for the scope it began in.
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
            return IsStill(scope) ? await write(scope, server, linked.Token).ConfigureAwait(false) : MonitorResult.Unavailable;
        }
        finally
        {
            _one.Release();
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
            return await ReadAgainAsync(scope, server, monitor.Id, cancellationToken).ConfigureAwait(false) is null && _book.Find(scope, monitor.Id) is null
                ? MonitorResult.Removed
                : MonitorResult.Changed;
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
            await SyncOneAsync(scope, server, monitor, cancellationToken).ConfigureAwait(false);
        }
        if (IsStill(scope))
        {
            await TakeRegisteredAsync(scope, server, cancellationToken).ConfigureAwait(false);
        }
    }

    // The stops still to go for the scope, each under the validator its monitor was last read with. One whose monitor changed
    // since (412, or 409 while the server applied another change) goes once more under its validator read again, as the Apple
    // app's: a stop does not depend on what changed. One the server will not take is dropped (the list shows the monitor again);
    // one that got no answer waits for the next run.
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
        lock (_gate)
        {
            if (_pendingStops.TryGetValue(scope, out var stops))
            {
                stops.Remove(subscriptionId);
            }
        }
    }

    // The monitor already takes what the choice asks for, running.
    private static bool Matches(DownloadSubscription monitor, MonitorChoice choice) =>
        monitor.Active && monitor.Mode == choice.Mode && monitor.DeleteWatched == choice.DeleteWatched
        && monitor.MaxStorageBytes == Math.Max(0, choice.MaxStorageBytes)
        && (choice.Quality is null || choice.Quality == (monitor.Quality ?? DownloadQualities.Original));

    // No answer, a conflict or a server error: the server may have made it.
    private static bool IsUncertain(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or SiloApiException { Status: (int)HttpStatusCode.Conflict or >= 500 }
        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);
}
