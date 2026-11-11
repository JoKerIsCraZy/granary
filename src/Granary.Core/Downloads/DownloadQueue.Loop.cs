using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>
/// The queue's loop: each pass looks at the folders when that is due, activates the scope with the server when it becomes the
/// active one, starts transfers, and hands what the server is to hear to the server work beside the loop.
/// </summary>
public sealed partial class DownloadQueue
{
    // One iteration never ends the loop: a pass that fails is logged and tried again, and so is the wait after it.
    private async Task LoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            Task woken;
            lock (_gate)
            {
                if (_wake.Task.IsCompleted)
                {
                    _wake = NewSignal();
                }
                woken = _wake.Task;
            }
            try
            {
                await PassAsync(stop).ConfigureAwait(false);
                _passRetryAt = null;
            }
            catch (Exception ex) when (IsStopping(ex, stop))
            {
                // The queue stops: the loop ends, quietly.
                break;
            }
            catch (Exception ex)
            {
                // A pass that failed is tried again a minute later, or at the next wake.
                _logger.LogError("A pass of the download queue failed ({Failure})", DownloadLog.Failure(ex));
                _passRetryAt = _time.GetUtcNow() + RetryDelays[^1];
            }
            finally
            {
                Interlocked.Increment(ref _passes);
            }
            try
            {
                await ParkAsync(woken, stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsStopping(ex, stop))
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError("The download queue could not tell when to look again ({Failure}); it waits for a wake", DownloadLog.Failure(ex));
                await WaitForWakeAsync(woken, RetryDelays[^1], stop).ConfigureAwait(false);
            }
        }
    }

    // What a stop leaves behind: a cancelled wait, or what the queue let go of in Dispose.
    private static bool IsStopping(Exception ex, CancellationToken stop) =>
        stop.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException;

    // Waits for a wake; a timer wakes the loop at the earliest time something waits for, and work that fell due while the
    // pass ran goes at once.
    private async Task ParkAsync(Task woken, CancellationToken stop)
    {
        var due = NextWake();
        if (due == TimeSpan.Zero)
        {
            return;
        }
        using var timer = due is { } wait ? _time.CreateTimer(static queue => ((DownloadQueue)queue!).Wake(), this, wait, Timeout.InfiniteTimeSpan) : null;
        lock (_gate)
        {
            if (!woken.IsCompleted)
            {
                _parked.TrySetResult();
                if (_running.Count == 0 && !_choresRunning && _rechecking.IsCompleted)
                {
                    _idle.TrySetResult();
                }
            }
        }
        await woken.WaitAsync(stop).ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.SuppressThrowing);
    }

    // The fallback when the next time could not be told: a wake, or the delay. It never throws.
    private async Task WaitForWakeAsync(Task woken, TimeSpan delay, CancellationToken stop)
    {
        try
        {
            using var timer = _time.CreateTimer(static queue => ((DownloadQueue)queue!).Wake(), this, delay, Timeout.InfiniteTimeSpan);
            await woken.WaitAsync(stop).ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.SuppressThrowing);
        }
        catch (Exception ex)
        {
            _logger.LogError("The download queue could not wait ({Failure})", DownloadLog.Failure(ex));
            await Task.Yield();
        }
    }

    private async Task PassAsync(CancellationToken stop)
    {
        await _store.LoadAsync(stop).ConfigureAwait(false);
        if (!_loaded)
        {
            _loaded = true;
            NoteInterrupted();
        }
        RecheckFoldersWhenDue();
        ForgetGone();
        DownloadScope? scope;
        Uri? server;
        long generation;
        bool stopped;
        bool cleared = false;
        bool resumed = false;
        lock (_gate)
        {
            // The scope, its server and the session's generation as one: a transfer is started for them, or not at all.
            (scope, server) = _current;
            generation = _sessionGeneration;
            stopped = scope is not null && IsStoppedLocked(scope, out resumed);
            if (scope is not null && !stopped && IsClearedLocked(scope))
            {
                cleared = true;
                foreach (var run in _running.Values.Where(r => r.Key.Scope == scope))
                {
                    CancelLocked(run);
                }
            }
        }
        bool activated = scope != _activeScope || resumed;
        if (scope is null || server is null || stopped)
        {
            _activeScope = scope;
            _runnable = false;
            return;
        }
        if (cleared)
        {
            // A sign-out took its downloads off this PC a moment ago: nothing more starts for it, not even an activation, and it
            // is activated anew once it is the current scope again after the sign-out.
            _activeScope = null;
            _runnable = false;
            return;
        }
        if (activated)
        {
            // The scope is the active one only once its activation went through: one that failed is done again a minute later,
            // capability and registry included, or at once for another scope or session; a wake before then asks nothing.
            _activeScope = null;
            _runnable = false;
            if (_activationRetry is { } retry && retry.Scope == scope && retry.Generation == generation && retry.At > _time.GetUtcNow())
            {
                return;
            }
            _activationRetry = new ActivationRetry(scope, generation, _time.GetUtcNow() + RetryDelays[^1]);
            await ActivateAsync(scope, server, stop).ConfigureAwait(false);
            _activationRetry = null;
        }
        _activeScope = scope;
        _runnable = true;
        if (!ReadWaitsForUnmetered())
        {
            StartingTransfers?.Invoke();
            StartTransfers(scope, server, generation);
        }
        StartChores(scope, server);
    }

    // The folders are read again at most every FolderRecheck after the last recheck ended (or when a transfer found its folder
    // gone), beside the loop, one recheck at a time: it reads the disk for every download, a slow drive's included. It also
    // retries removed downloads' folders that are due, and wakes the loop once all of it is done, for the deletes those folders
    // held back.
    private void RecheckFoldersWhenDue()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_stopping || !_rechecking.IsCompleted || (!_recheckRequested && _lastRecheck is { } last && now - last < FolderRecheck))
            {
                return;
            }
            _recheckRequested = false;
            _rechecking = Task.Run(RecheckFoldersAsync);
            // The loop is woken once the recheck is over, not from inside it: a pass it wakes sees it done.
            _ = _rechecking.ContinueWith(static (_, queue) => ((DownloadQueue)queue!).Wake(), this, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    // Never throws: a recheck that fails is logged, and the next one is a minute later. The next is counted from its end, so a
    // recheck slower than FolderRecheck is never due again while it runs.
    private async Task RecheckFoldersAsync()
    {
        try
        {
            _store.RecheckFolders();
            ForgetHeldFolders();
            await _store.FolderWork.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Looking at the downloads' folders failed ({Failure})", DownloadLog.Failure(ex));
        }
        finally
        {
            ForgetHeldFolders();
            lock (_gate)
            {
                _lastRecheck = _time.GetUtcNow();
            }
        }
    }

    // A recheck deletes held folders again: whether they are still there is for the server work to find out once more.
    private void ForgetHeldFolders()
    {
        lock (_gate)
        {
            _heldFolders.Clear();
        }
    }

    // What a run before this one left: a transfer still Downloading was interrupted, and resumes with the margin; the bytes of
    // every unfinished download are what its part holds.
    private void NoteInterrupted()
    {
        foreach (var download in _store.All.Where(d => !d.FilesMissing && d.Record.State != DownloadState.Completed))
        {
            var key = Key.Of(download.Record);
            long length = PartLength(download);
            lock (_gate)
            {
                if (download.Record.State == DownloadState.Downloading)
                {
                    _resumeWithMargin.Add(key);
                }
                _bytes.TryAdd(key, length);
            }
        }
    }

    // What the queue keeps of downloads that are gone goes with them.
    private void ForgetGone()
    {
        var kept = _store.All.Select(d => Key.Of(d.Record)).ToHashSet();
        lock (_gate)
        {
            foreach (var key in _bytes.Keys.Concat(_retries.Keys).Concat(_reportRetries.Keys).Concat(_progressSaid.Keys)
                .Where(k => !kept.Contains(k) && !_running.ContainsKey(k)).ToList())
            {
                _bytes.Remove(key);
                _retries.Remove(key);
                _reportRetries.Remove(key);
                _progressSaid.Remove(key);
                _integrityFailed.Remove(key);
                _resumeWithMargin.Remove(key);
            }
        }
    }

    // Starts the scope's transfers, while it is still the active one: a session change since the pass took it has the next pass
    // start the new scope's, and none of this one's.
    private void StartTransfers(DownloadScope scope, Uri server, long generation)
    {
        var now = _time.GetUtcNow();
        foreach (var download in _store.In(scope).Where(IsWanted).OrderBy(d => d.Record.CreatedAt))
        {
            var key = Key.Of(download.Record);
            lock (_gate)
            {
                if (_stopping || _running.Count >= MaxConcurrent || _current.Scope != scope || _current.Server != server || IsClearedLocked(scope))
                {
                    return;
                }
                if (_running.ContainsKey(key) || _resetting.Contains(key) || (_retries.TryGetValue(key, out var retry) && retry.At > now))
                {
                    continue;
                }
                var run = new Running(key, server, download.Record.Revision, generation);
                _running[key] = run;
                run.Task = Task.Run(() => RunAsync(run));
            }
        }
    }

    // The server work for the scope, when some is due and none runs: manifests to refresh, the registry to read, deletes and
    // reports to send. The loop does not wait for it; it wakes the loop when it is done.
    private void StartChores(DownloadScope scope, Uri server)
    {
        if (!AreChoresDue(scope))
        {
            return;
        }
        lock (_gate)
        {
            if (_stopping || _choresRunning)
            {
                return;
            }
            _choresRunning = true;
            _chores = Task.Run(() => RunChoresAsync(scope, server, _stopToken));
        }
    }

    private bool AreChoresDue(DownloadScope scope)
    {
        var now = _time.GetUtcNow();
        var downloads = _store.In(scope);
        bool deletes = _store.PendingDeletes(scope).Any(d => IsSendable(scope, d));
        bool preparing = downloads.Any(d => !d.FilesMissing && d.Record.State == DownloadState.Preparing);
        lock (_gate)
        {
            if (_choresRetryAt is { } retryAt && retryAt > now)
            {
                return false;
            }
            return _refreshFor == scope
                || IsReconcileDueLocked(preparing, now)
                || (deletes && (_deleteRetry is not { } delete || delete.At <= now))
                || downloads.Any(d => d.Record.PendingReport is not null && IsReportDueLocked(Key.Of(d.Record), now));
        }
    }

    // A report not on its way (or asked for by its own sender), and not waiting out its delay after one that could not go.
    private bool IsReportDueLocked(Key key, DateTimeOffset now, bool sending = false) =>
        (sending || !_reporting.ContainsKey(key)) && (!_reportRetries.TryGetValue(key, out var retry) || retry.At <= now);

    // Each part for the scope it was started for, while that scope is still the active one and no sign-out took its downloads.
    // One that fails unexpectedly holds the work back for a minute, so it does not come back at once.
    private async Task RunChoresAsync(DownloadScope scope, Uri server, CancellationToken stop)
    {
        try
        {
            bool refresh;
            lock (_gate)
            {
                refresh = _refreshFor == scope;
                _refreshFor = null;
            }
            if (refresh)
            {
                await RefreshManifestsAsync(scope, server, stop).ConfigureAwait(false);
            }
            if (IsStillActive(scope) && IsReconcileDue(scope))
            {
                await ReconcileAsync(scope, server, stop).ConfigureAwait(false);
            }
            if (IsStillActive(scope))
            {
                await SendPendingDeletesAsync(scope, server, stop).ConfigureAwait(false);
            }
            if (IsStillActive(scope))
            {
                await SendPendingReportsAsync(scope, server, stop).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsStopping(ex, stop))
        {
            // The queue stops.
        }
        catch (Exception ex)
        {
            _logger.LogError("The download queue's work with the server failed ({Failure}); it is tried again in a minute", DownloadLog.Failure(ex));
            lock (_gate)
            {
                _choresRetryAt = _time.GetUtcNow() + RetryDelays[^1];
            }
        }
        finally
        {
            lock (_gate)
            {
                _choresRunning = false;
                WakeLocked();
            }
        }
    }

    // The current scope still, and no sign-out took its downloads since it became so.
    private bool IsStillActive(DownloadScope scope)
    {
        lock (_gate)
        {
            return _current.Scope == scope && !IsClearedLocked(scope);
        }
    }

    // How long until the loop has to look again: a pass that failed, and for a scope whose downloads can run, a download's
    // retry while a transfer can start, a missing folder's recheck and, while no server work runs, the next look at preparing
    // copies, a registry read, a delete or a report to try again. Zero when one of them fell due while the pass ran: the
    // next pass acts on each of these (or starts the server work, which wakes the loop when it ends), so it never comes back
    // at once for the same one.
    private TimeSpan? NextWake()
    {
        var now = _time.GetUtcNow();
        var scope = _activeScope;
        bool runnable = _runnable && scope is not null;
        var downloads = runnable ? _store.In(scope!) : [];
        var pendingDeletes = runnable ? _store.PendingDeletes(scope!) : [];
        bool deletes = pendingDeletes.Any(d => IsSendable(scope!, d));
        // A removed download's folder something holds or whose drive is away, of any profile and with a server delete or
        // without: a recheck tries it again when it is due (and a delete waiting for it goes after).
        var folderDelete = _store.NextFolderDeleteAt;
        bool metered = runnable && ReadWaitsForUnmetered();
        DateTimeOffset? next = _passRetryAt;
        if (_activationRetry is { } activation)
        {
            next = Earliest(next, activation.At);
        }
        lock (_gate)
        {
            bool canStart = !metered && !_stopping && _running.Count < MaxConcurrent;
            foreach (var download in downloads)
            {
                var key = Key.Of(download.Record);
                if (canStart && IsWanted(download) && !_running.ContainsKey(key) && !_resetting.Contains(key) && _retries.TryGetValue(key, out var retry))
                {
                    next = Earliest(next, retry.At);
                }
            }
            // A recheck under way wakes the loop when it ends: until then, no other is due.
            var recheck = (_lastRecheck ?? now) + FolderRecheck;
            bool rechecking = !_rechecking.IsCompleted;
            if (!rechecking && downloads.Any(d => d.FilesMissing && !d.IsDamaged))
            {
                next = Earliest(next, recheck);
            }
            if (!rechecking && folderDelete is { } tryAt)
            {
                // Never before a recheck is due: one that is not would be no look at all.
                next = Earliest(next, tryAt > recheck ? tryAt : recheck);
            }
            if (runnable && !_choresRunning)
            {
                next = NextChoreLocked(next, downloads, deletes, now);
            }
        }
        return next is not { } at ? null : at > now ? at - now : TimeSpan.Zero;
    }

    private DateTimeOffset? NextChoreLocked(DateTimeOffset? next, IReadOnlyList<StoredDownload> downloads, bool deletes, DateTimeOffset now)
    {
        if (_choresRetryAt is { } retryAt && retryAt > now)
        {
            return Earliest(next, retryAt);
        }
        if (downloads.Any(d => !d.FilesMissing && d.Record.State == DownloadState.Preparing))
        {
            next = Earliest(next, (_lastReconcile ?? now) + PreparingPoll);
        }
        if (_reconcileRequested && _reconcileRetry is { } reconcile)
        {
            next = Earliest(next, reconcile.At);
        }
        if (deletes && _deleteRetry is { } delete)
        {
            next = Earliest(next, delete.At);
        }
        foreach (var download in downloads.Where(d => d.Record.PendingReport is not null))
        {
            var key = Key.Of(download.Record);
            if (!_reporting.ContainsKey(key) && _reportRetries.TryGetValue(key, out var report))
            {
                next = Earliest(next, report.At);
            }
        }
        return next;
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? next, DateTimeOffset at) => next is { } earlier && earlier <= at ? earlier : at;

    // A scope whose sign-in failed waits until the session changed; then it is active again, as at a profile switch.
    private bool IsStoppedLocked(DownloadScope scope, out bool resumed)
    {
        resumed = false;
        if (!_stoppedScopes.TryGetValue(scope, out long generation))
        {
            return false;
        }
        if (generation == _sessionGeneration)
        {
            return true;
        }
        _stoppedScopes.Remove(scope);
        resumed = true;
        return false;
    }
}
