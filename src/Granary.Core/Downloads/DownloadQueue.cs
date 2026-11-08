using Granary.Core.Api.Downloads;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>
/// Runs the active profile's downloads (docs/architecture.md, "The queue"): at most <see cref="MaxConcurrent"/> transfers at
/// once, oldest first. One loop decides what starts, on a wake: <see cref="Wake"/>, a session change, the network or this PC's
/// settings changing, the store changing, a transfer ending, or the earliest time something waits for. Transfers stop at once
/// when their download is deleted or paused, the connection turns metered without the setting, the profile changes, or the
/// queue stops; each keeps its bytes and carries on with <c>Range</c> and <c>If-Range</c>.
/// </summary>
/// <remarks>
/// Event handlers only cancel and wake: the loop and the transfers run on the thread pool, never on the thread that raised the
/// event. Every task the queue starts is awaited by it or by <see cref="StopAsync"/>, and logs what it does not expect.
/// </remarks>
public sealed partial class DownloadQueue : IDisposable
{
    /// <summary>Transfers at once, as Silo's Apple app runs them.</summary>
    public const int MaxConcurrent = 2;

    /// <summary>
    /// What a transfer found interrupted at the start gives up of its part: at most this much was written but not yet on the
    /// disk when Granary or Windows stopped (the transfer flushes every 3 MiB of 1 MiB writes).
    /// </summary>
    public const long ResumeMargin = 4L * 1024 * 1024;

    /// <summary>How long <see cref="StopAsync"/> waits for the transfers to let go of their files.</summary>
    public static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(2);

    /// <summary>The waits after a lost connection or a missing drive, one after another; the last repeats.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [.. new[] { 1, 2, 4, 8, 16, 32, 60 }.Select(seconds => TimeSpan.FromSeconds(seconds))];

    /// <summary>The longest a busy server's <c>Retry-After</c> is waited for; one asking for hours is asked again after this.</summary>
    public static readonly TimeSpan MaxBusyWait = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);

    private readonly DownloadStore _store;
    private readonly DownloadTransfer _transfer;
    private readonly DownloadAssets _assets;
    private readonly DownloadsApi _api;
    private readonly ISessionManager _session;
    private readonly INetworkLink _network;
    private readonly IDownloadPreferences _preferences;
    private readonly TimeProvider _time;
    private readonly ILogger<DownloadQueue> _logger;

    // Guards every field below; the store and the handlers' callers are never called while it is held, except the store's
    // reads, which take only the store's own lock.
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<Key, Running> _running = [];
    private readonly Dictionary<Key, long> _bytes = [];
    private readonly Dictionary<Key, DateTimeOffset> _progressSaid = [];
    private readonly Dictionary<Key, Retry> _retries = [];
    private readonly Dictionary<Key, Retry> _reportRetries = [];
    private readonly HashSet<Key> _integrityFailed = [];
    private readonly HashSet<Key> _resumeWithMargin = [];
    private readonly HashSet<Key> _reporting = [];
    private readonly HashSet<Key> _reportAgain = [];

    // Downloads whose files a new revision replaces: nothing starts them meanwhile.
    private readonly HashSet<Key> _resetting = [];

    // One reconcile at a time, the loop's or a caller's.
    private readonly SemaphoreSlim _reconciling = new(1, 1);
    private Capability? _capability;
    private bool _reconcileRequested;
    private DateTimeOffset? _lastReconcile;
    private Retry? _deleteRetry;

    // A scope whose sign-in failed waits for the session to change from the generation it failed in.
    private readonly Dictionary<DownloadScope, long> _stoppedScopes = [];
    private long _sessionGeneration;
    private (DownloadScope? Scope, Uri? Server) _current;
    private TaskCompletionSource _wake = NewSignal();
    private TaskCompletionSource _parked = NewSignal();
    private TaskCompletionSource _idle = NewSignal();
    private Task _loop = Task.CompletedTask;
    private DateTimeOffset? _passRetryAt;
    private bool _started;
    private bool _stopping;

    // The loop's own: whether the store was read, the scope it last ran, and whether that scope's downloads can run.
    private bool _loaded;
    private DownloadScope? _activeScope;
    private bool _runnable;

    public DownloadQueue(DownloadStore store, DownloadTransfer transfer, DownloadAssets assets, DownloadsApi api, ISessionManager session,
        INetworkLink network, IDownloadPreferences preferences, TimeProvider time, ILogger<DownloadQueue> logger)
    {
        _store = store;
        _transfer = transfer;
        _assets = assets;
        _api = api;
        _session = session;
        _network = network;
        _preferences = preferences;
        _time = time;
        _logger = logger;
    }

    /// <summary>A download's bytes on disk changed; raised on a transfer's thread, at most every half second for each.</summary>
    public event EventHandler<string>? Progressed;

    /// <summary>Downloads wait now because the connection is metered and Settings → Downloads does not allow it.</summary>
    public bool IsWaitingForUnmetered => _network.Current.IsMetered && !_preferences.DownloadsOnMetered;

    /// <summary>For tests: done once the loop waits with nothing to do and no transfer running.</summary>
    internal Task Idle
    {
        get
        {
            lock (_gate)
            {
                return _idle.Task;
            }
        }
    }

    /// <summary>For tests: done once the loop waits with nothing to do, transfers running or not.</summary>
    internal Task Parked
    {
        get
        {
            lock (_gate)
            {
                return _parked.Task;
            }
        }
    }

    /// <summary>Starts following the session, the network, the settings and the store, and the loop. Once.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _stopping)
            {
                return;
            }
            _started = true;
            _current = (DownloadScope.Of(_session), _session.ActiveServer?.BaseUri);
        }
        _session.Changed += OnSessionChanged;
        _network.Changed += OnConditionsChanged;
        _preferences.Changed += OnConditionsChanged;
        _store.Changed += OnStoreChanged;
        lock (_gate)
        {
            _loop = Task.Run(() => LoopAsync(_stop.Token));
        }
    }

    /// <summary>Has the loop look at the downloads again, as after one was added, paused or resumed.</summary>
    public void Wake()
    {
        lock (_gate)
        {
            WakeLocked();
        }
    }

    /// <summary>The bytes of a download on this PC so far, as its transfer last wrote them; 0 when none are known.</summary>
    public long BytesDone(DownloadScope scope, string downloadId)
    {
        lock (_gate)
        {
            return _bytes.GetValueOrDefault(new Key(scope, downloadId));
        }
    }

    /// <summary>
    /// Stops the loop and every transfer, and waits up to <see cref="StopBudget"/> for them to let go of their files. A transfer
    /// stopped here stays <see cref="DownloadState.Downloading"/>, so the next start resumes it with <see cref="ResumeMargin"/>.
    /// </summary>
    public async Task StopAsync()
    {
        Task[] tasks;
        lock (_gate)
        {
            _stopping = true;
            foreach (var run in _running.Values)
            {
                CancelLocked(run);
            }
            tasks = [_loop, .. _running.Values.Select(r => r.Task)];
        }
        Unsubscribe();
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(tasks).WaitAsync(StopBudget, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("The downloads did not stop within {Seconds} s", StopBudget.TotalSeconds);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopping = true;
            foreach (var run in _running.Values)
            {
                CancelLocked(run);
            }
        }
        Unsubscribe();
        _stop.Cancel();
    }

    private void Unsubscribe()
    {
        _session.Changed -= OnSessionChanged;
        _network.Changed -= OnConditionsChanged;
        _preferences.Changed -= OnConditionsChanged;
        _store.Changed -= OnStoreChanged;
    }

    // Raised on the UI thread: the new scope is taken here, and the other scopes' transfers stop.
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        var scope = DownloadScope.Of(_session);
        var server = _session.ActiveServer?.BaseUri;
        lock (_gate)
        {
            _sessionGeneration++;
            _current = (scope, server);
            foreach (var run in _running.Values.Where(r => r.Key.Scope != scope))
            {
                CancelLocked(run);
            }
            WakeLocked();
        }
    }

    // The link or the settings: on a metered link without the setting, everything stops.
    private void OnConditionsChanged(object? sender, EventArgs e)
    {
        bool waits = IsWaitingForUnmetered;
        lock (_gate)
        {
            if (waits)
            {
                foreach (var run in _running.Values)
                {
                    CancelLocked(run);
                }
            }
            WakeLocked();
        }
    }

    // A transfer whose download went, was paused or no longer is to run stops at once, so its part is let go before a delete
    // removes the folder.
    private void OnStoreChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            foreach (var run in _running.Values)
            {
                if (_store.Find(run.Key.Scope, run.Key.Id) is not { } download || !IsWanted(download))
                {
                    CancelLocked(run);
                }
            }
            WakeLocked();
        }
    }

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
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A pass that failed is tried again a minute later, or at the next wake.
                _logger.LogError("A pass of the download queue failed ({Failure})", DownloadLog.Failure(ex));
                _passRetryAt = _time.GetUtcNow() + RetryDelays[^1];
            }
            await ParkAsync(woken, stop).ConfigureAwait(false);
        }
    }

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
                if (_running.Count == 0)
                {
                    _idle.TrySetResult();
                }
            }
        }
        await woken.WaitAsync(stop).ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task PassAsync(CancellationToken stop)
    {
        await _store.LoadAsync(stop).ConfigureAwait(false);
        if (!_loaded)
        {
            _loaded = true;
            NoteInterrupted();
        }
        _store.RecheckFolders();
        // Removed downloads' folders a recheck deletes again go before their server deletes are looked at.
        await _store.FolderWork.ConfigureAwait(false);
        ForgetGone();
        DownloadScope? scope;
        Uri? server;
        bool stopped;
        bool resumed = false;
        lock (_gate)
        {
            (scope, server) = _current;
            stopped = scope is not null && IsStoppedLocked(scope, out resumed);
        }
        bool activated = scope != _activeScope || resumed;
        _activeScope = scope;
        _runnable = scope is not null && server is not null && !stopped;
        if (scope is null || server is null || stopped)
        {
            return;
        }
        if (activated)
        {
            await ActivateAsync(scope, server, stop).ConfigureAwait(false);
        }
        else if (IsReconcileDue(scope))
        {
            await ReconcileAsync(scope, server, stop).ConfigureAwait(false);
        }
        await SendPendingDeletesAsync(scope, server, stop).ConfigureAwait(false);
        await SendPendingReportsAsync(scope, server, stop).ConfigureAwait(false);
        if (IsWaitingForUnmetered)
        {
            return;
        }
        StartTransfers(scope, server);
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

    private void StartTransfers(DownloadScope scope, Uri server)
    {
        var now = _time.GetUtcNow();
        foreach (var download in _store.In(scope).Where(IsWanted).OrderBy(d => d.Record.CreatedAt))
        {
            var key = Key.Of(download.Record);
            lock (_gate)
            {
                if (_stopping || _running.Count >= MaxConcurrent)
                {
                    return;
                }
                if (_running.ContainsKey(key) || _resetting.Contains(key) || (_retries.TryGetValue(key, out var retry) && retry.At > now))
                {
                    continue;
                }
                var run = new Running(key, server, download.Record.Revision);
                _running[key] = run;
                run.Task = Task.Run(() => RunAsync(run));
            }
        }
    }

    // How long until the loop has to look again: a pass that failed, and for a scope whose downloads can run, the next look at
    // preparing copies, a delete's retry, a download's retry while a transfer can start, a report's retry while none is under
    // way, and a missing folder's recheck. Zero when one of them fell due while the pass ran: the next pass acts on each of
    // these, so it never comes back at once for the same one.
    private TimeSpan? NextWake()
    {
        var now = _time.GetUtcNow();
        var scope = _activeScope;
        bool runnable = _runnable && scope is not null;
        var downloads = runnable ? _store.In(scope!) : [];
        bool deletes = runnable && _store.PendingDeletes(scope!).Count > 0;
        bool metered = runnable && IsWaitingForUnmetered;
        DateTimeOffset? next = _passRetryAt;
        lock (_gate)
        {
            bool canStart = !metered && !_stopping && _running.Count < MaxConcurrent;
            if (downloads.Any(d => !d.FilesMissing && d.Record.State == DownloadState.Preparing))
            {
                next = Earliest(next, (_lastReconcile ?? now) + PreparingPoll);
            }
            if (deletes && _deleteRetry is { } deleteRetry)
            {
                next = Earliest(next, deleteRetry.At);
            }
            foreach (var download in downloads)
            {
                var key = Key.Of(download.Record);
                if (canStart && IsWanted(download) && !_running.ContainsKey(key) && !_resetting.Contains(key) && _retries.TryGetValue(key, out var retry))
                {
                    next = Earliest(next, retry.At);
                }
                if (download.Record.PendingReport is not null && !_reporting.Contains(key) && _reportRetries.TryGetValue(key, out var report))
                {
                    next = Earliest(next, report.At);
                }
                if (download.FilesMissing && !download.IsDamaged)
                {
                    next = Earliest(next, now + RetryDelays[^1]);
                }
            }
        }
        return next is not { } at ? null : at > now ? at - now : TimeSpan.Zero;
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? next, DateTimeOffset at) => next is { } earlier && earlier <= at ? earlier : at;

    // A download that is to be on this PC and can be fetched now: queued or under way, not paused, gone or missing.
    private static bool IsWanted(StoredDownload download) =>
        !download.FilesMissing
        && download.Record is { State: DownloadState.Queued or DownloadState.Downloading, IsPausedByViewer: false, IsNoLongerAvailable: false };

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

    private void WakeLocked()
    {
        _wake.TrySetResult();
        if (_parked.Task.IsCompleted)
        {
            _parked = NewSignal();
        }
        if (_idle.Task.IsCompleted)
        {
            _idle = NewSignal();
        }
    }

    // Cancelled off this thread: a handler on the UI thread never runs a transfer's continuation.
    private void CancelLocked(Running run)
    {
        if (run.Cancel.IsCancellationRequested)
        {
            return;
        }
        var cancelled = run.Cancel.CancelAsync();
        if (!cancelled.IsCompletedSuccessfully)
        {
            _ = cancelled.ContinueWith(
                static (task, state) => ((ILogger)state!).LogWarning("Stopping a download failed ({Failure})", DownloadLog.Failure(task.Exception!)),
                _logger, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static long PartLength(StoredDownload download)
    {
        try
        {
            var part = new FileInfo(download.PartPath);
            return part.Exists ? part.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A download, by its scope and id: download ids are only unique on their server.</summary>
    private readonly record struct Key(DownloadScope Scope, string Id)
    {
        public static Key Of(DownloadRecord record) => new(record.Scope, record.DownloadId);
    }

    /// <summary>When a download, or its status report, is tried again, and how many tries in a row failed.</summary>
    private readonly record struct Retry(DateTimeOffset At, int Failures);

    /// <summary>What the capability of the active scope says the queue does: ask <c>/file-proxy</c>, delete revoked copies.</summary>
    private sealed record Capability(DownloadScope Scope, bool ViaProxy, bool RevokedRemovesLocal);

    /// <summary>One transfer under way, for the revision of the download it started with.</summary>
    private sealed class Running(Key key, Uri server, long revision)
    {
        public Key Key { get; } = key;

        public Uri Server { get; } = server;

        public long Revision { get; } = revision;

        public CancellationTokenSource Cancel { get; } = new();

        public Task Task { get; set; } = Task.CompletedTask;
    }
}
