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
/// <c>refreshWatchState</c>); the Android app sends none. Only the active profile's entries go, bound to that account and
/// profile, so another profile's wait until it is used again. A run belongs to the session it began for: once another
/// server, account or profile is the active one, it sends and reads nothing more. Once the server took positions, the pages that show where the viewer
/// left off are told (<see cref="ViewerStateChanges"/>), as a stream's stop tells them.
/// </summary>
/// <remarks>
/// One run at a time (<c>_running</c>), on the thread pool, and at most one more waiting for it, which takes every wish made
/// meanwhile; <see cref="FlushSoon"/> and the session's and the link's changes only set a timer. A run claims entries from the
/// queue, which keeps its own lock, and changes the downloads through the store. Nothing here is awaited by the player.
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

    /// <summary>
    /// The most requests one run spends on halving batches the server refused, to find the entries it refuses: about twice
    /// the halvings one bad entry among <see cref="MaxBatch"/> takes, and room for a few more. What a run does not get to waits
    /// for the next.
    /// </summary>
    public const int MaxSplitSends = 32;

    // A listing longer than this many pages is not followed to its end (the Apple app's bound).
    private const int MaxPages = 100;

    private readonly OfflineProgressQueue _queue;
    private readonly ProgressSyncApi _api;
    private readonly DownloadStore _store;
    private readonly ISessionManager _session;
    private readonly INetworkLink _network;
    private readonly ViewerStateChanges _changes;
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

    // The run that waits for the one under way; null once it began. A server that takes a request and never answers holds a
    // run for the whole HTTP timeout, and the player asks for a flush every ten seconds.
    private Task? _waiting;

    // Only a run touches these: whose progress was read last, and when.
    private DownloadScope? _readFor;
    private long _readAt;

    public OfflineProgressSync(OfflineProgressQueue queue, ProgressSyncApi api, DownloadStore store, ISessionManager session, INetworkLink network,
        ViewerStateChanges changes, TimeProvider time, ILogger<OfflineProgressSync> logger)
    {
        _queue = queue;
        _api = api;
        _store = store;
        _session = session;
        _network = network;
        _changes = changes;
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
        // Off this thread, which is the UI's at the app's start: the player's first look at the queue finds it read.
        _ = _queue.LoadAsync();
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
            if (_disposed)
            {
                return _latestRun = Task.CompletedTask;
            }
            // A run that has not begun sends what is there when it begins: this wish is its own too.
            return _latestRun = _waiting ??= Task.Run(RunGuardedAsync);
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
                _waiting = null;
                readBack = _wantsReadBack;
                _wantsReadBack = false;
            }
            await RunAsync(readBack, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Granary closes: what was claimed is in the file as it was, and goes with the next start's send.
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

    // Whose positions the session would send now, and where to: the server's entry is read once, so its id and its address
    // are the same server's. Null unless the session is ready with a server, an account and a profile.
    private Target? Acting()
    {
        var server = _session.ActiveServer;
        return _session.State == SessionState.Ready && server is not null && _session.Account is { } account
            && _session.ProfileId is { Length: > 0 } profile
            ? new Target(new DownloadScope(server.Id, account.Id, profile), server.BaseUri)
            : null;
    }

    // The session is still the one the run began for. Asked before every request: a run can take minutes (batches, up to a
    // hundred pages), and a sign-out and another sign-in fit into that. The request itself is bound to the account and the
    // profile as well, for a change between this look and the send (SiloRequestOptions.Account, .Profile).
    private bool IsStill(Target target) => Acting() == target;

    private async Task RunAsync(bool readBack, CancellationToken cancellationToken)
    {
        if (Acting() is not { } target)
        {
            return;
        }
        var (scope, server) = target;
        // The downloads are read once per run of the app; a run before that waits for them.
        await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        bool sent = false;
        var halving = new SplitBudget();
        while (IsStill(target))
        {
            var batch = _queue.Claim(scope, MaxBatch);
            if (batch.Count == 0)
            {
                break;
            }
            var outcome = SendOutcome.TryAgain;
            try
            {
                outcome = await SendHalvingAsync(target, batch, halving, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Whatever ended the send without an outcome (Granary closing, something unexpected), its entries wait for
                // the next send rather than stay on their way for the rest of the run.
                _queue.Resolve(outcome.Done, OfflineProgressOutcome.Done);
                _queue.Resolve([.. batch.Except(outcome.Done)], OfflineProgressOutcome.Later);
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
        if (sent)
        {
            TellThePages();
        }
        if (readBack && IsReadBackDue(scope, sent) && await ReadBackAsync(target, cancellationToken).ConfigureAwait(false) > 0 && !sent)
        {
            // The server had taken positions whose answer never came: it has them all the same.
            TellThePages();
        }
    }

    // The server has where the viewer left a download: Home's Continue Watching and Next Up and the title's page are stale,
    // as after a stream's stop (ViewerStateChanges), also when the position went long after the play, once the connection
    // was back. Not while a title plays: its stop tells the pages, and a download's stop waits for this very send
    // (LocalPlayback.StopAsync), so they read the server once it has the position. The server's own event for the send is
    // then this app's echo (RealtimeEvents.EchoWindow).
    private void TellThePages()
    {
        if (!_changes.IsPlaying)
        {
            _changes.Report();
        }
    }

    // A batch, and when the server refuses the request itself (400, 422), its halves, and theirs, until the entries it
    // refuses stand alone: the server fails a whole request for one item it cannot read, and the other 99 are not wrong for
    // that. Only an entry refused by itself is dropped. One bad entry among a hundred costs 14 more requests; a run spends
    // at most MaxSplitSends on it, and what it did not get to waits for the next run. Each half is sent only while the
    // session is still the run's.
    private async Task<SendOutcome> SendHalvingAsync(Target target, IReadOnlyList<OfflineProgressEntry> batch, SplitBudget halving,
        CancellationToken cancellationToken)
    {
        var outcome = await SendAsync(target.Server, target.Scope, batch, cancellationToken).ConfigureAwait(false);
        if (!outcome.IsRefused)
        {
            return outcome;
        }
        if (batch.Count == 1)
        {
            _logger.LogWarning("The server refused 1 position(s) for good; it is dropped");
            return new SendOutcome(batch, Reached: false, GoesOn: true);
        }
        var done = new List<OfflineProgressEntry>();
        bool reached = false;
        int half = batch.Count / 2;
        foreach (var part in new[] { batch.Take(half).ToList(), batch.Skip(half).ToList() })
        {
            if (halving.Left <= 0 || !IsStill(target))
            {
                return new SendOutcome(done, reached, GoesOn: false);
            }
            halving.Left--;
            var sent = await SendHalvingAsync(target, part, halving, cancellationToken).ConfigureAwait(false);
            done.AddRange(sent.Done);
            reached |= sent.Reached;
            if (!sent.GoesOn)
            {
                return new SendOutcome(done, reached, GoesOn: false);
            }
        }
        return new SendOutcome(done, reached, GoesOn: true);
    }

    // One request. The answer decides what becomes of its entries (docs/api-contract.md, 14.7): an item the server answered
    // for with success leaves, and so does one it will never take (a title the profile cannot see); a request it will never
    // take (400, 422) is refused, which is the caller's to narrow down. Everything else is sent again with the next try: an item that failed on the server's
    // side, a server that refuses or fails for now (any other status), a request that did not get there, and one whose
    // answer never came or says nothing usable. The server may have taken that last kind, and the second send does no harm:
    // each position carries the time it was recorded, and the server passes over one that is not newer than what it has
    // (silo-server's SetProgressIfNewer), without counting a watch.
    private async Task<SendOutcome> SendAsync(Uri server, DownloadScope scope, IReadOnlyList<OfflineProgressEntry> batch, CancellationToken cancellationToken)
    {
        var body = new ProgressSyncBody([.. batch.Select(e => new ProgressSyncItem(e.MediaItemId, e.PositionMs, e.DurationMs, e.UpdatedAt))]);
        try
        {
            var result = await _api.SyncAsync(server, scope.AccountId, scope.ProfileId, body, cancellationToken).ConfigureAwait(false);
            if (result.Items.Count != batch.Count || result.Items.Where((item, index) => item.MediaItemId is { } id && id != batch[index].MediaItemId).Any())
            {
                // An answer that is not for what was sent says nothing sure about any of it.
                _logger.LogWarning("The server answered for {Answered} of {Sent} positions; they are sent again", result.Items.Count, batch.Count);
                return SendOutcome.TryAgain;
            }
            var done = batch.Where((_, index) => result.Items[index] is { Status: ProgressSyncItemResult.Success } or { IsRefusedForGood: true }).ToList();
            int taken = result.Items.Count(i => i.Status == ProgressSyncItemResult.Success);
            _logger.LogInformation("Sent {Count} position(s) recorded on this PC: the server took {Taken}, refused {Refused} for good, and failed on {Failed}",
                batch.Count, taken, done.Count - taken, batch.Count - done.Count);
            // With items to send again, the run ends here: the next batch would only claim them once more.
            return new SendOutcome(done, Reached: taken > 0, GoesOn: done.Count == batch.Count);
        }
        catch (ProfileChangedException)
        {
            return SendOutcome.TryAgain;
        }
        catch (SiloApiException ex) when (ex.Status is 400 or 422)
        {
            _logger.LogInformation("The server refused a request with {Count} position(s) ({Failure})", batch.Count, ApiFailureText.Describe(ex));
            return SendOutcome.Refused;
        }
        catch (SiloApiException ex)
        {
            // A sign-in to renew, a busy or failing server, a proxy or a maintenance page in its place, an older server
            // without the route: none of them says the positions are wrong.
            _logger.LogInformation("The server does not take positions now ({Failure}); they wait", ApiFailureText.Describe(ex));
            return SendOutcome.TryAgain;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogInformation("No usable answer came for {Count} position(s) ({Failure}); they are sent again", batch.Count, ApiFailureText.Describe(ex));
            return SendOutcome.TryAgain;
        }
    }

    // Wanted, and worth a listing: the profile has downloads to bring up to date, and either this run sent something the
    // server may have judged, or the last read is another profile's or old enough.
    private bool IsReadBackDue(DownloadScope scope, bool sent) =>
        _store.In(scope).Count > 0 && (sent || _readFor != scope || _time.GetElapsedTime(_readAt) >= ReadBackInterval);

    // The whole listing, then the downloads it names: the later of the server's position and this PC's holds, the server's
    // on a tie, with its word on whether the title is watched (the Apple app's mergeProgress). This PC's holds only while
    // it still waits to be sent: once the server answered for it, the server's row is what became of it, also an older one
    // (a position too short for the server to keep, a title its threshold does not count as watched). A listing that
    // breaks off changes nothing. A complete one also settles what still waits in the queue: a position the server has (its row is as
    // new, or newer) leaves, as one whose send was taken while its answer got lost; every other one is sent again.
    // Answers how many waiting positions the server turned out to have.
    private async Task<int> ReadBackAsync(Target target, CancellationToken cancellationToken)
    {
        var (scope, server) = target;
        var rows = new Dictionary<string, ProgressListItem>(StringComparer.Ordinal);
        try
        {
            string? cursor = null;
            for (int page = 0; ; page++)
            {
                if (!IsStill(target))
                {
                    _logger.LogInformation("Another server, account or profile became the active one while the progress was read; nothing is taken from it");
                    return 0;
                }
                var listing = await _api.ListAsync(server, scope.AccountId, scope.ProfileId, cursor, cancellationToken).ConfigureAwait(false);
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
                    return 0;
                }
                cursor = next;
            }
        }
        catch (Exception ex) when (ex is ProfileChangedException || ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            _logger.LogInformation("The server's progress could not be read ({Failure}); this PC keeps what it has", ApiFailureText.Describe(ex));
            return 0;
        }
        if (!IsStill(target))
        {
            return 0;
        }
        foreach (var download in _store.In(scope))
        {
            if (rows.TryGetValue(download.Record.MediaItemId, out var row) && double.IsFinite(row.PositionSeconds) && !KeepsItsOwn(download.Record, row)
                && Changes(download.Record, row))
            {
                await TakeAsync(scope, download.Record.DownloadId, row, cancellationToken).ConfigureAwait(false);
            }
        }
        int settled = _queue.DropKnown(scope,
            rows.ToDictionary(row => row.Key, row => new KnownProgress(row.Value.UpdatedAt, Milliseconds(row.Value)), StringComparer.Ordinal));
        if (settled > 0)
        {
            _logger.LogInformation("The server already had {Count} position(s) that waited on this PC", settled);
        }
        _readFor = scope;
        _readAt = _time.GetTimestamp();
        return settled;
    }

    // This PC recorded a newer position than the row's and the server has not answered for it yet: it is the later one.
    private bool KeepsItsOwn(DownloadRecord record, ProgressListItem row) =>
        record.ResumeAt is { } local && local > row.UpdatedAt && _queue.HasUnsent(record.Scope, record.MediaItemId);

    // A row that says what the download already has is no change: nothing is written and nobody who follows the downloads
    // hears of it.
    private static bool Changes(DownloadRecord record, ProgressListItem row) =>
        record.ResumeMs != Milliseconds(row) || record.ResumeAt != row.UpdatedAt || record.IsWatched != IsWatched(row);

    private static long Milliseconds(ProgressListItem row) => (long)Math.Round(Math.Max(0, row.PositionSeconds) * 1000);

    // The server counts the title as watched and sets its position back to 0; "completed" stays through a later watch, whose
    // position then stands beside it. Only the first is a title the viewer is through with (DownloadRecord.IsWatched).
    private static bool IsWatched(ProgressListItem row) => row.Completed && Milliseconds(row) == 0;

    private async Task TakeAsync(DownloadScope scope, string downloadId, ProgressListItem row, CancellationToken cancellationToken)
    {
        long positionMs = Milliseconds(row);
        try
        {
            // Looked at again as it is written: a position the play recorded since then is the later one.
            await _store.UpdateAsync(scope, downloadId, record => KeepsItsOwn(record, row)
                ? record
                : record with { ResumeMs = positionMs, ResumeAt = row.UpdatedAt, IsWatched = IsWatched(row) }, cancellationToken).ConfigureAwait(false);
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

    /// <summary>The session a run began for: whose positions it sends, and the server they go to.</summary>
    private sealed record Target(DownloadScope Scope, Uri Server);

    /// <summary>
    /// What a send did: the entries that leave the queue (every other one of its batch is sent again), whether the server took
    /// any position, and whether the next batch may go.
    /// </summary>
    private sealed record SendOutcome(IReadOnlyList<OfflineProgressEntry> Done, bool Reached, bool GoesOn)
    {
        public static readonly SendOutcome TryAgain = new([], Reached: false, GoesOn: false);

        /// <summary>The server refused the request itself: which of its entries it will not take is not known yet.</summary>
        public static readonly SendOutcome Refused = new([], Reached: false, GoesOn: false) { IsRefused = true };

        public bool IsRefused { get; private init; }
    }

    /// <summary>The requests a run may still spend on halving refused batches (<see cref="MaxSplitSends"/>).</summary>
    private sealed class SplitBudget
    {
        public int Left { get; set; } = MaxSplitSends;
    }
}
