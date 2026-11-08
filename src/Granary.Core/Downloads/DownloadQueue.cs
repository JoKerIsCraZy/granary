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
/// queue stops; each keeps its bytes and carries on with <c>Range</c> and <c>If-Range</c>. What the server is told and asked
/// besides the files (reports, deletes, the registry) runs beside the loop, so it never holds a transfer back.
/// </summary>
/// <remarks>
/// Event handlers only cancel and wake: the loop, the transfers and the server work run on the thread pool, never on the
/// thread that raised the event. Every task the queue starts is awaited by it or by <see cref="StopAsync"/>, and logs what it
/// does not expect.
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

    /// <summary>
    /// The waits after a lost connection, a missing drive, or a busy server that names no wait of a second or more, one after
    /// another; the last repeats.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [.. new[] { 1, 2, 4, 8, 16, 32, 60 }.Select(seconds => TimeSpan.FromSeconds(seconds))];

    /// <summary>The longest a busy server's <c>Retry-After</c> is waited for; one asking for hours is asked again after this.</summary>
    public static readonly TimeSpan MaxBusyWait = TimeSpan.FromMinutes(5);

    /// <summary>How often the folders are looked at again (a drive plugged in or pulled out), and a held folder deleted again.</summary>
    public static readonly TimeSpan FolderRecheck = TimeSpan.FromSeconds(60);

    /// <summary>How long a transfer waits for its "downloading" report before it carries on without it.</summary>
    public static readonly TimeSpan ReportBudget = TimeSpan.FromSeconds(15);

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
    private readonly CancellationToken _stopToken;
    private readonly Dictionary<Key, Running> _running = [];
    private readonly Dictionary<Key, long> _bytes = [];
    private readonly Dictionary<Key, DateTimeOffset> _progressSaid = [];
    private readonly Dictionary<Key, Retry> _retries = [];
    private readonly Dictionary<Key, Retry> _reportRetries = [];
    private readonly HashSet<Key> _integrityFailed = [];
    private readonly HashSet<Key> _resumeWithMargin = [];
    // The send under way for each download's reports, and what it does when it ends.
    private readonly Dictionary<Key, TaskCompletionSource> _reporting = [];
    private readonly HashSet<Key> _reportAgain = [];

    // Downloads whose files a new revision replaces: nothing starts them meanwhile.
    private readonly HashSet<Key> _resetting = [];

    // Removed downloads' folders the server work found still there: their deletes wait for the next folder recheck.
    private readonly HashSet<string> _heldFolders = new(StringComparer.OrdinalIgnoreCase);

    // One reconcile at a time, the loop's, the server work's or a caller's.
    private readonly SemaphoreSlim _reconciling = new(1, 1);
    private Capability? _capability;
    private bool _reconcileRequested;
    private Retry? _reconcileRetry;
    private DateTimeOffset? _lastReconcile;
    private Retry? _deleteRetry;

    // The server work beside the loop: whether it runs, its task, the scope whose manifests it refreshes, and when it may run
    // again after it failed.
    private bool _choresRunning;
    private Task _chores = Task.CompletedTask;
    private DownloadScope? _refreshFor;
    private DateTimeOffset? _choresRetryAt;

    // A scope whose sign-in failed waits for the session to change from the generation its transfer started in.
    private readonly Dictionary<DownloadScope, long> _stoppedScopes = [];
    private long _sessionGeneration;
    private (DownloadScope? Scope, Uri? Server) _current;
    private TaskCompletionSource _wake = NewSignal();
    private TaskCompletionSource _parked = NewSignal();
    private TaskCompletionSource _idle = NewSignal();
    private Task _loop = Task.CompletedTask;
    private DateTimeOffset? _lastRecheck;
    private bool _recheckRequested;
    private bool _started;
    private bool _stopping;
    private bool _disposed;
    private long _passes;

    // The loop's own: whether the store was read, the scope it last ran, whether that scope's downloads can run, and when a
    // pass that failed is tried again.
    private bool _loaded;
    private DownloadScope? _activeScope;
    private bool _runnable;
    private DateTimeOffset? _passRetryAt;

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
        _stopToken = _stop.Token;
    }

    /// <summary>A download's bytes on disk changed; raised on a transfer's thread, at most every half second for each.</summary>
    public event EventHandler<string>? Progressed;

    /// <summary>Downloads wait now because the connection is metered and Settings → Downloads does not allow it.</summary>
    public bool IsWaitingForUnmetered => _network.Current.IsMetered && !_preferences.DownloadsOnMetered;

    /// <summary>For tests: done once the loop waits with nothing to do, no transfer running and no server work.</summary>
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

    /// <summary>For tests: how many passes the loop ended, failed ones included.</summary>
    internal long Passes => Interlocked.Read(ref _passes);

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
        }
        // Following first: a change while the scope is read below is not missed.
        _session.Changed += OnSessionChanged;
        _network.Changed += OnConditionsChanged;
        _preferences.Changed += OnConditionsChanged;
        _store.Changed += OnStoreChanged;
        var scope = DownloadScope.Of(_session);
        var server = _session.ActiveServer?.BaseUri;
        lock (_gate)
        {
            _current = (scope, server);
            _loop = Task.Run(() => LoopAsync(_stopToken));
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

    /// <summary>
    /// The viewer resumed a download: what it waited out (a retry delay, a first failed integrity check) is forgotten, and the
    /// loop looks again.
    /// </summary>
    internal void RetryNow(DownloadScope scope, string downloadId)
    {
        var key = new Key(scope, downloadId);
        lock (_gate)
        {
            _retries.Remove(key);
            _integrityFailed.Remove(key);
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
    /// Stops the loop, every transfer and the server work, and waits up to <see cref="StopBudget"/> for them to let go of their
    /// files. A transfer stopped here stays <see cref="DownloadState.Downloading"/>, so the next start resumes it with
    /// <see cref="ResumeMargin"/>. It never throws: what did not stop in time, or failed, is logged.
    /// </summary>
    public async Task StopAsync()
    {
        Task[] tasks;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _stopping = true;
            foreach (var run in _running.Values)
            {
                CancelLocked(run);
            }
            tasks = [_loop, _chores, .. _running.Values.Select(r => r.Task)];
        }
        Unsubscribe();
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Dispose came first, and cancelled already.
        }
        var stopped = Task.WhenAll(tasks).WaitAsync(StopBudget, _time);
        await stopped.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (stopped.Exception?.InnerException is TimeoutException)
        {
            _logger.LogWarning("The downloads did not stop within {Seconds} s", StopBudget.TotalSeconds);
            return;
        }
        foreach (var failed in tasks.Where(t => t.IsFaulted))
        {
            _logger.LogError("Part of the download queue failed as it stopped ({Failure})", DownloadLog.Failure(failed.Exception!.GetBaseException()));
        }
    }

    /// <summary>Stops everything at once, without waiting, and lets go of what the queue holds. Twice is no change.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _stopping = true;
            foreach (var run in _running.Values)
            {
                CancelLocked(run);
            }
        }
        Unsubscribe();
        _stop.Cancel();
        // What still runs sees the stop on its own token; the loop's timer goes with the loop's wait.
        _stop.Dispose();
        _reconciling.Dispose();
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

    // A download that is to be on this PC and can be fetched now: queued or under way, not paused, gone or missing.
    private static bool IsWanted(StoredDownload download) =>
        !download.FilesMissing
        && download.Record is { State: DownloadState.Queued or DownloadState.Downloading, IsPausedByViewer: false, IsNoLongerAvailable: false };

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

    /// <summary>When a download, a report, a delete or a registry read is tried again, and how many tries in a row failed.</summary>
    private readonly record struct Retry(DateTimeOffset At, int Failures);

    /// <summary>What the capability of the active scope says the queue does: ask <c>/file-proxy</c>, delete revoked copies.</summary>
    private sealed record Capability(DownloadScope Scope, bool ViaProxy, bool RevokedRemovesLocal);

    /// <summary>
    /// One transfer under way, for the revision of the download it started with, in the session's generation at its start: a
    /// sign-in it finds refused stops the profile only until the session changes from that one.
    /// </summary>
    private sealed class Running(Key key, Uri server, long revision, long generation)
    {
        public Key Key { get; } = key;

        public Uri Server { get; } = server;

        public long Revision { get; } = revision;

        public long Generation { get; } = generation;

        public CancellationTokenSource Cancel { get; } = new();

        public Task Task { get; set; } = Task.CompletedTask;
    }
}
