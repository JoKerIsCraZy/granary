using Granary.Core.Api.Downloads;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>What a monitor of a series is to take, as the download dialog's Monitor chose it.</summary>
/// <param name="Quality">The preset; null leaves it to the server (original), as for a server without <c>monitor_quality</c>.</param>
public sealed record MonitorChoice(string Mode, bool DeleteWatched, long MaxStorageBytes, string? Quality);

/// <summary>
/// A change to a monitor: <paramref name="Mode"/> and <paramref name="Quality"/> null when they stay as they are (an unchanged
/// mode or quality is never sent again); the rest is sent as it is, and the monitor runs again (<c>active</c>).
/// </summary>
public sealed record MonitorEdit(string? Mode, bool DeleteWatched, long MaxStorageBytes, string? Quality);

/// <summary>What a monitor's create, change or stop came to.</summary>
public enum MonitorResult
{
    Saved,

    /// <summary>The monitor changed on the server since it was read: it was read again, and nothing more was sent.</summary>
    Changed,

    /// <summary>The server no longer has the monitor.</summary>
    Removed,

    /// <summary>No answer said whether it went through, and the list could not tell.</summary>
    Uncertain,

    /// <summary>The server refused it, or could not be asked.</summary>
    Failed,

    /// <summary>No profile is signed in, or its account is being signed out.</summary>
    Unavailable,
}

/// <summary>
/// The active profile's series monitors (docs/architecture.md, "Series monitoring"), as Silo's Apple app keeps them
/// (<c>DownloadManager.swift</c>, "Series monitoring"): the server registers a monitored series' episodes only when Granary
/// syncs, so a run reads the capability and the monitor list, sends the stops still to go, syncs each active monitor, takes
/// the episodes the server registered into the downloads (they then come as any other), and deletes the episodes a monitor
/// with Delete after watching finished. A run comes at the start, when another profile becomes the active one, every
/// <see cref="SyncInterval"/> while Granary runs, after a monitor is made or changed, and when asked
/// (<see cref="SyncSoon"/>, as for a notification). The monitors themselves are the server's: signing out leaves them there.
/// </summary>
/// <remarks>
/// One thing at a time (<c>_one</c>): a run, a create, a change or a stop waits for the one before it, so a list read never
/// misses a write that landed while it was read, which the Apple app keeps a ledger of. Everything runs on the thread pool
/// and answers for the scope it began in: once another server, account or profile is the active one, it applies nothing.
/// </remarks>
public sealed partial class SeriesMonitors : IDisposable
{
    /// <summary>How often a run comes while Granary runs, as the Apple app's background refresh asks for one at the earliest.</summary>
    public static readonly TimeSpan SyncInterval = TimeSpan.FromHours(4);

    /// <summary>How long a wish for a run waits, so what asks at once (a burst of notifications) is one run.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    private readonly DownloadSubscriptionsApi _api;
    private readonly DownloadsApi _downloads;
    private readonly DownloadStore _store;
    private readonly MonitorBook _book;
    private readonly DownloadManager _manager;
    private readonly ISessionManager _session;
    private readonly TimeProvider _time;
    private readonly ILogger<SeriesMonitors> _logger;
    private readonly OfflineProgressSync? _progress;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    // Under _gate.
    private readonly Lock _gate = new();
    private readonly Dictionary<DownloadScope, Dictionary<string, string>> _pendingStops = [];
    private ITimer? _settle;
    private ITimer? _interval;
    private DownloadScope? _following;
    private bool _started;
    private bool _disposed;
    private bool _running;
    private bool _runAgain;
    private Task _latestRun = Task.CompletedTask;

    public SeriesMonitors(DownloadSubscriptionsApi api, DownloadsApi downloads, DownloadStore store, MonitorBook book, DownloadManager manager,
        ISessionManager session, TimeProvider time, ILogger<SeriesMonitors> logger, OfflineProgressSync? progress = null)
    {
        _api = api;
        _downloads = downloads;
        _store = store;
        _book = book;
        _manager = manager;
        _session = session;
        _time = time;
        _logger = logger;
        _progress = progress;
    }

    /// <summary>Raised on any thread when the active profile's monitors may have changed.</summary>
    public event EventHandler? Changed
    {
        add => _book.Changed += value;
        remove => _book.Changed -= value;
    }

    /// <summary>The active profile's monitors as last read, newest first; paused ones too.</summary>
    public IReadOnlyList<DownloadSubscription> Monitors => DownloadScope.Of(_session) is { } scope ? _book.In(scope) : [];

    /// <summary>The active profile's monitor of <paramref name="seriesId"/>, paused or not.</summary>
    public DownloadSubscription? For(string seriesId) => DownloadScope.Of(_session) is { } scope ? _book.ForSeries(scope, seriesId) : null;

    /// <summary>The series' title as Granary knows it from a download dialog or a download; null when it knows none.</summary>
    public string? TitleOf(string seriesId) => DownloadScope.Of(_session) is { } scope ? _book.TitleOf(scope, seriesId) : null;

    /// <summary>The run started most recently; done once it ended, however it ended.</summary>
    public Task LatestRun
    {
        get
        {
            lock (_gate)
            {
                return _latestRun;
            }
        }
    }

    /// <summary>Follows the session from now on and runs every <see cref="SyncInterval"/>; the start itself runs soon. Once.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }
            _started = true;
        }
        // Following first: a profile switch while the scope is read below is not missed.
        _session.Changed += OnSessionChanged;
        lock (_gate)
        {
            if (_disposed)
            {
                // Disposed meanwhile: it unsubscribed before this subscribed, so this lets go again.
                _session.Changed -= OnSessionChanged;
                return;
            }
            _following = DownloadScope.Of(_session);
            _interval = _time.CreateTimer(static self => ((SeriesMonitors)self!).SyncSoon(), this, SyncInterval, SyncInterval);
        }
        SyncSoon();
    }

    /// <summary>
    /// A run in a moment (<see cref="Settle"/>) after the first wish; wishes made meanwhile are that one run, so a stream of them
    /// never puts it off.
    /// </summary>
    public void SyncSoon()
    {
        lock (_gate)
        {
            if (_disposed || _settle is not null)
            {
                return;
            }
            _settle = _time.CreateTimer(static self => ((SeriesMonitors)self!).Kick(), this, Settle, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Runs now, after whatever runs; done when the run is.</summary>
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _one.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await RunOnceAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _settle?.Dispose();
            _interval?.Dispose();
        }
        _session.Changed -= OnSessionChanged;
        _stop.Cancel();
    }

    // Another server, account or profile: its monitors are read and synced. Signed out, there is nothing to run.
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        var scope = DownloadScope.Of(_session);
        lock (_gate)
        {
            if (scope == _following)
            {
                return;
            }
            _following = scope;
        }
        if (scope is not null)
        {
            SyncSoon();
        }
    }

    // A timer's wish: one run at a time, and one more after it for every wish made while it ran.
    private void Kick()
    {
        lock (_gate)
        {
            _settle?.Dispose();
            _settle = null;
            if (_disposed)
            {
                return;
            }
            if (_running)
            {
                _runAgain = true;
                return;
            }
            _running = true;
            _latestRun = Task.Run(RunWhileAskedAsync);
        }
    }

    private async Task RunWhileAskedAsync()
    {
        while (true)
        {
            try
            {
                await SyncAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                // Granary closes.
            }
            catch (Exception ex)
            {
                // A run that fails is logged; the next wish runs again.
                _logger.LogError("A run of the series monitors failed ({Failure})", DownloadLog.Failure(ex));
            }
            lock (_gate)
            {
                if (!_runAgain || _disposed)
                {
                    _running = false;
                    return;
                }
                _runAgain = false;
            }
        }
    }

    // The scope a run or a write is for, and its server; none without a ready session.
    private (DownloadScope Scope, Uri Server)? Target() =>
        DownloadScope.Of(_session) is { } scope && _session.ActiveServer?.BaseUri is { } server ? (scope, server) : null;

    private bool IsStill(DownloadScope scope) => DownloadScope.Of(_session) == scope;
}
