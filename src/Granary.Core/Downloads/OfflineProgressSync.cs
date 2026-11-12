using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Granary.Core.Auth;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>
/// Sends what was watched from this PC to the server and reads the server's progress back (docs/architecture.md, "Offline
/// progress"; docs/api-contract.md, 14.7), as Silo's Apple app does (<c>DownloadManager.flushProgressQueue</c> and
/// <c>refreshWatchState</c>); the Android app sends none. Only the active profile's entries go, bound to that profile, so
/// another profile's wait until it is used again.
/// </summary>
/// <remarks>
/// One run at a time (<c>_running</c>), on the thread pool; <see cref="FlushSoon"/> and the session's and the link's changes
/// only set a timer. A run claims entries from the queue, which keeps its own lock, and changes the downloads through the
/// store. Nothing here is awaited by the player.
/// </remarks>
public sealed class OfflineProgressSync : IDisposable
{
    /// <summary>The most entries one request carries (<see cref="ProgressSyncApi.MaxItems"/>).</summary>
    public const int MaxBatch = ProgressSyncApi.MaxItems;

    /// <summary>How long a wish to flush waits, so the changes of one moment go in one run.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    /// <summary>How long entries the server would not take yet wait for the next try.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The server's progress is read again this long after the last read at the earliest (the Apple app's
    /// <c>watchStateReadInterval</c>), unless a run sent something and its caller wants the server's word on it.
    /// </summary>
    public static readonly TimeSpan ReadBackInterval = TimeSpan.FromMinutes(10);

    // A listing longer than this many pages is not followed to its end (the Apple app's bound).
    private const int MaxPages = 100;

    private readonly OfflineProgressQueue _queue;
    private readonly ProgressSyncApi _api;
    private readonly DownloadStore _store;
    private readonly ISessionManager _session;
    private readonly INetworkLink _network;
    private readonly TimeProvider _time;
    private readonly ILogger<OfflineProgressSync> _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly CancellationTokenSource _stopped = new();

    // Under _gate.
    private ITimer? _settle;
    private ITimer? _retry;
    private bool _wantsReadBack;
    private bool _started;
    private bool _disposed;
    private Task _latestRun = Task.CompletedTask;

    // Only a run touches these: whose progress was read last, and when.
    private DownloadScope? _readFor;
    private long _readAt;

    public OfflineProgressSync(OfflineProgressQueue queue, ProgressSyncApi api, DownloadStore store, ISessionManager session, INetworkLink network,
        TimeProvider time, ILogger<OfflineProgressSync> logger)
    {
        _queue = queue;
        _api = api;
        _store = store;
        _session = session;
        _network = network;
        _time = time;
        _logger = logger;
    }

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

    /// <summary>
    /// Follows the session and the connection from now on: a profile that becomes active, a server that is signed in to and
    /// a link that changes each flush what waits and read the server's progress back, and so does the start itself.
    /// </summary>
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
        _session.Changed += OnChanged;
        _network.Changed += OnChanged;
        FlushSoon(readBack: true);
    }

    /// <summary>
    /// Flushes in a moment (<see cref="Settle"/>). With <paramref name="readBack"/>, the server's progress is read after it:
    /// the end of a play asks for it, to learn whether the server now counts the title as watched.
    /// </summary>
    public void FlushSoon(bool readBack = false)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _wantsReadBack |= readBack;
            _settle?.Dispose();
            _settle = _time.CreateTimer(_ => Run(), null, Settle, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Flushes now, and with <paramref name="readBack"/> reads the server's progress after it; done when the run is.</summary>
    public Task FlushAsync(bool readBack, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _wantsReadBack |= readBack;
        }
        return Run().WaitAsync(cancellationToken);
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
            _retry?.Dispose();
        }
        _session.Changed -= OnChanged;
        _network.Changed -= OnChanged;
        _stopped.Cancel();
    }

    // Raised on any thread.
    private void OnChanged(object? sender, EventArgs e) => FlushSoon(readBack: true);

    private Task Run()
    {
        lock (_gate)
        {
            return _latestRun = _disposed ? Task.CompletedTask : Task.Run(RunGuardedAsync);
        }
    }

    private async Task RunGuardedAsync()
    {
        var cancellationToken = _stopped.Token;
        try
        {
            await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            bool readBack;
            lock (_gate)
            {
                readBack = _wantsReadBack;
                _wantsReadBack = false;
            }
            await RunAsync(readBack, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Granary closes: what was claimed is held at the next start.
        }
        catch (Exception ex)
        {
            // Nobody waits for a run: what it did not expect is logged by its kind (a file error's message names a folder).
            _logger.LogError("Syncing the positions recorded on this PC failed unexpectedly ({Failure})", DownloadLog.Failure(ex));
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task RunAsync(bool readBack, CancellationToken cancellationToken)
    {
        if (DownloadScope.Of(_session) is not { } scope || _session.ActiveServer?.BaseUri is not { } server)
        {
            return;
        }
        // The downloads are read once per run of the app; a run before that waits for them.
        await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        bool sent = false;
        while (true)
        {
            var batch = _queue.Claim(scope, MaxBatch);
            if (batch.Count == 0)
            {
                break;
            }
            // On the disk as on their way before they leave: a Granary that closes now does not send them twice.
            await _queue.LatestSave.ConfigureAwait(false);
            SendOutcome? outcome = null;
            try
            {
                outcome = await SendAsync(server, scope, batch, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Whatever ended the send without an outcome (Granary closing, something unexpected), the request may have
                // left: its entries are held rather than left on their way for the rest of the run.
                _queue.Resolve(batch, outcome?.Entries ?? OfflineProgressOutcome.Uncertain);
            }
            sent |= outcome.Reached;
            if (!outcome.GoesOn)
            {
                break;
            }
        }
        if (_queue.Entries(scope).Any(e => e.State == OfflineProgressState.Pending))
        {
            RetryLater();
        }
        if (readBack && IsReadBackDue(scope, sent))
        {
            await ReadBackAsync(server, scope, cancellationToken).ConfigureAwait(false);
        }
    }

    // One request, sent once. The answer decides what becomes of its entries (the Apple app's outcome model): answered,
    // they leave, each item's success or failure alike; a server that asks to come back, or a request that never left,
    // keeps them for later; a request that left without a usable answer holds them, since the server may have taken them
    // and a second send could count a watch twice; a request the server will never take is dropped.
    private async Task<SendOutcome> SendAsync(Uri server, DownloadScope scope, IReadOnlyList<OfflineProgressEntry> batch, CancellationToken cancellationToken)
    {
        var body = new ProgressSyncBody([.. batch.Select(e => new ProgressSyncItem(e.MediaItemId, e.PositionMs, e.DurationMs, e.UpdatedAt))]);
        try
        {
            var result = await _api.SyncAsync(server, scope.ProfileId, body, cancellationToken).ConfigureAwait(false);
            if (result.Items.Count != batch.Count)
            {
                _logger.LogWarning("The server answered for {Answered} of {Sent} positions; they are held", result.Items.Count, batch.Count);
                return SendOutcome.Uncertain;
            }
            int failed = result.Items.Count(i => i.Status != ProgressSyncItemResult.Success);
            _logger.LogInformation("Sent {Count} position(s) recorded on this PC; the server refused {Failed}", batch.Count, failed);
            return SendOutcome.Answered;
        }
        catch (ProfileChangedException)
        {
            return SendOutcome.Later;
        }
        catch (SiloApiException ex) when (ex.Status is 401 or 403 or 408 or 429 or 502 or 503)
        {
            _logger.LogInformation("The server does not take positions now ({Failure}); they wait", ApiFailureText.Describe(ex));
            return SendOutcome.Later;
        }
        catch (SiloApiException ex) when (ex.Status >= 500)
        {
            // The server failed while it had the request, or a gateway gave up waiting for it: it may have been applied.
            _logger.LogWarning("The server failed on {Count} position(s) ({Failure}); they are held", batch.Count, ApiFailureText.Describe(ex));
            return SendOutcome.Uncertain;
        }
        catch (SiloApiException ex)
        {
            _logger.LogWarning("The server refused {Count} position(s) for good ({Failure}); they are dropped", batch.Count, ApiFailureText.Describe(ex));
            return SendOutcome.Refused;
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
            or HttpRequestError.SecureConnectionError)
        {
            // The request never left this PC.
            return SendOutcome.Later;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning("No usable answer came for {Count} position(s) ({Failure}); they are held", batch.Count, ApiFailureText.Describe(ex));
            return SendOutcome.Uncertain;
        }
    }

    // Wanted, and worth a listing: the profile has downloads to bring up to date, and either this run sent something the
    // server may have judged, or the last read is another profile's or old enough.
    private bool IsReadBackDue(DownloadScope scope, bool sent) =>
        _store.In(scope).Count > 0 && (sent || _readFor != scope || _time.GetElapsedTime(_readAt) >= ReadBackInterval);

    // The whole listing, then the downloads it names: the later of the server's position and this PC's holds, the server's
    // on a tie, with its word on whether the title is watched (the Apple app's mergeProgress). A listing that breaks off
    // changes nothing. A complete one also lets the held entries go: what the server has is known now.
    private async Task ReadBackAsync(Uri server, DownloadScope scope, CancellationToken cancellationToken)
    {
        var rows = new Dictionary<string, ProgressListItem>(StringComparer.Ordinal);
        try
        {
            string? cursor = null;
            for (int page = 0; ; page++)
            {
                var listing = await _api.ListAsync(server, scope.ProfileId, cursor, cancellationToken).ConfigureAwait(false);
                foreach (var row in listing.Items)
                {
                    if (row.MediaItemId is { Length: > 0 } id && (!rows.TryGetValue(id, out var known) || row.UpdatedAt > known.UpdatedAt))
                    {
                        rows[id] = row;
                    }
                }
                if (listing.Page is not { HasMore: true, NextCursor: { Length: > 0 } next })
                {
                    break;
                }
                if (next == cursor || page + 1 >= MaxPages)
                {
                    _logger.LogWarning("The server's progress listing did not end; nothing is taken from it");
                    return;
                }
                cursor = next;
            }
        }
        catch (Exception ex) when (ex is ProfileChangedException || ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            _logger.LogInformation("The server's progress could not be read ({Failure}); this PC keeps what it has", ApiFailureText.Describe(ex));
            return;
        }
        foreach (var download in _store.In(scope))
        {
            if (rows.TryGetValue(download.Record.MediaItemId, out var row) && double.IsFinite(row.PositionSeconds))
            {
                await TakeAsync(scope, download.Record.DownloadId, row, cancellationToken).ConfigureAwait(false);
            }
        }
        _queue.DropHeld(scope);
        _readFor = scope;
        _readAt = _time.GetTimestamp();
    }

    private async Task TakeAsync(DownloadScope scope, string downloadId, ProgressListItem row, CancellationToken cancellationToken)
    {
        long positionMs = (long)Math.Round(Math.Max(0, row.PositionSeconds) * 1000);
        try
        {
            await _store.UpdateAsync(scope, downloadId, record => record.ResumeAt is { } local && local > row.UpdatedAt
                ? record
                : record with { ResumeMs = positionMs, ResumeAt = row.UpdatedAt, IsWatched = row.Completed }, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            // Deleted meanwhile.
        }
    }

    private void RetryLater()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _retry?.Dispose();
            _retry = _time.CreateTimer(_ => FlushSoon(readBack: true), null, RetryInterval, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>What a send did: what becomes of its entries, whether the server took the request, and whether the next batch may go.</summary>
    private sealed record SendOutcome(OfflineProgressOutcome Entries, bool Reached, bool GoesOn)
    {
        public static readonly SendOutcome Answered = new(OfflineProgressOutcome.Done, Reached: true, GoesOn: true);
        public static readonly SendOutcome Later = new(OfflineProgressOutcome.Later, Reached: false, GoesOn: false);
        public static readonly SendOutcome Uncertain = new(OfflineProgressOutcome.Uncertain, Reached: false, GoesOn: false);
        public static readonly SendOutcome Refused = new(OfflineProgressOutcome.Done, Reached: false, GoesOn: false);
    }
}
