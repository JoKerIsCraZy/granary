using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>
/// The episodes a season's or a series' download could not register past the server's limit of downloads for the account (429
/// <c>rate_limited</c>, docs/architecture.md, "Waiting episodes"), kept in line in the store (<see cref="WaitingEpisode"/>) and
/// asked for again, the first first, one create each through <see cref="StartAsync(DownloadRequest, CancellationToken)"/> with
/// its guards, for the profile they were refused for and only while it is the active one. The server's 429 says neither which
/// limit it is (the downloads at once, which only an entry the server prepares holds, or the downloads in a period, which only
/// time frees) nor when to come back, so the line is asked again once one of the profile's downloads leaves its preparation
/// (the server's file is ready) or its delete reached the server (the server drops a device's entry, which then counts toward
/// neither limit), or after a wait (<see cref="WaitingRetryDelays"/>, or the server's <c>Retry-After</c> when it names a longer
/// one, kept to <see cref="MaxNamedWait"/>), and then as far as the server takes them. A refusal waits longer each time; a create
/// that got no clear answer, or a run that failed, waits as one. One the server refuses for good (a 4xx it means, an answer
/// that cannot be read) leaves the line, so it never holds the others back. At a start, or a switch to the profile, the line is
/// asked once, then waits.
/// </summary>
/// <remarks>
/// One run at a time (<see cref="WaitingRun"/>); a trigger while one runs has it look once more. The state below is guarded by
/// the manager's <c>_gate</c>; no store write, create or timer callback runs while it is held, and of the store only its reads,
/// which take the store's own lock inside it (never the other way round). The store's handlers only note and start a run: what
/// the run does happens on the thread pool.
/// </remarks>
public sealed partial class DownloadManager
{
    /// <summary>The waits after a refusal for the limit, one after another; the last repeats.</summary>
    public static readonly IReadOnlyList<TimeSpan> WaitingRetryDelays =
        [.. new[] { 1, 2, 5, 10, 30 }.Select(minutes => TimeSpan.FromMinutes(minutes))];

    /// <summary>
    /// How often in a row the line's head may fail without a clear answer (no answer, a 5xx, an answer that is no Silo answer)
    /// before it goes to the back of the line: it may have reached the server, so it is not dropped, but it holds the others
    /// back no longer.
    /// </summary>
    public const int HeadTries = 3;

    /// <summary>
    /// The longest wait a server's <c>Retry-After</c> sets: one far ahead, as a date years away, is asked again after this, as
    /// the sign-in's and the token refresh's waits are kept.
    /// </summary>
    public static readonly TimeSpan MaxNamedWait = TimeSpan.FromMinutes(30);

    private bool _draining;
    private bool _drainAgain;
    private Task _drain = Task.CompletedTask;

    // The profile whose create the limit refused last, how often in a row, the earliest the line is asked again, and how many
    // places had freed for its account (_freeGenerations) as that create was sent: a place freed after it lets the line be
    // asked at once. The server's limit is the account's, so the places are counted per server and account: one another
    // profile of the account frees counts, one another account frees does not.
    private DownloadScope? _refusedScope;
    private int _refusals;
    private DateTimeOffset _notBefore;
    private long _refusedAtFree;
    private readonly Dictionary<(string Server, string Account), long> _freeGenerations = [];

    // The head that failed without a clear answer, and how often in a row.
    private (string? EpisodeId, int Count) _headFailures;

    // The timer that ends the wait, and when it is due; null once it fired or while none is set.
    private ITimer? _retryTimer;
    private DateTimeOffset? _timerDue;

    // The active profile's downloads as the last store change left them while episodes waited: the ones the server prepares,
    // and the deletes still to reach it.
    private DownloadScope? _seenScope;
    private HashSet<string> _seenPreparing = [];
    private HashSet<string> _seenDeletes = [];

    /// <summary>For tests: the run asking the waiting episodes again, done once it looked at the line and stopped.</summary>
    internal Task WaitingRun
    {
        get
        {
            lock (_gate)
            {
                return _drain;
            }
        }
    }

    // How many places the server freed for the scope's account so far; taken before a create, compared after a refusal.
    private long FreeGenerationOf(DownloadScope scope)
    {
        lock (_gate)
        {
            return FreedLocked(scope);
        }
    }

    private long FreedLocked(DownloadScope scope) => _freeGenerations.GetValueOrDefault((scope.ServerEntryId, scope.AccountId));

    /// <summary>
    /// How many of the active profile's episodes wait for the server's limit of downloads; 0 without a profile.
    /// </summary>
    public int WaitingCount() => DownloadScope.Of(_session) is { } scope ? _store.WaitingIn(scope).Count : 0;

    /// <summary>The active profile's episode <paramref name="mediaItemId"/> waits for the server's limit: it is on its way here.</summary>
    public bool IsWaiting(string mediaItemId) => DownloadScope.Of(_session) is { } scope && _store.IsWaiting(scope, mediaItemId);

    /// <summary>
    /// Takes the waiting episodes of <paramref name="seriesId"/> (every series when null) out of <paramref name="scope"/>'s line:
    /// the viewer cancelled them, or deletes the series' downloads. It never waits for a create of the line under way, which may
    /// hang on the network: that create keeps its download only while its episode is still in line, asked in the store's own
    /// change, so its download is either here before this answers (and a delete that reads the downloads after it takes it
    /// along) or never kept, the server hearing the delete of what it made.
    /// </summary>
    public async Task DropWaitingAsync(DownloadScope scope, string? seriesId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        int dropped = await _store.RemoveWaitingAsync(scope, w => seriesId is null || w.SeriesId == seriesId, cancellationToken).ConfigureAwait(false);
        if (dropped > 0)
        {
            _logger.LogInformation("{Count} waiting episodes were taken out of line", dropped);
        }
    }

    // The episodes from the one the limit refused on wait in line, after the wait the refusal starts: the line is not asked
    // again before it, unless a place frees. The wait is noted before the episodes go in line, so the store's change does not
    // ask at once, and its timer is set once they are in it. A run the viewer stopped (stopped) by the time they would go in
    // line keeps none of them, asked in the store's change itself. Answers how many of the series' episodes wait as that
    // change left the line, counted in the change itself, and how many of them it put there. Should the index then not be
    // written, the episodes still wait in memory, and are counted as they are.
    private async Task<(int Added, int Waiting)> KeepWaitingAsync(DownloadScope scope, string seriesId, string seriesTitle, string quality,
        IEnumerable<(int Season, SeasonEpisode Episode)> episodes, long clears, TimeSpan? retryAfter, long freed, CancellationToken stopped)
    {
        var wait = BeginRefusal(scope, retryAfter, freed);
        List<WaitingEpisode> line = [.. episodes.Select(e => new WaitingEpisode(scope, seriesId, seriesTitle, e.Season, e.Episode.EpisodeId,
            e.Episode.EpisodeNumber, e.Episode.Title, e.Episode.MediaFileId, quality))];
        try
        {
            return await _store.AddWaitingUnlessClearedAsync(line, clears, stopped, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The episodes past the server's download limit could not be kept waiting ({Failure})", DownloadLog.Failure(ex));
            var now = _store.WaitingIn(scope).Where(w => w.SeriesId == seriesId).ToList();
            return (now.Count(w => line.Any(l => l.EpisodeId == w.EpisodeId)), now.Count);
        }
        finally
        {
            ArmRetry(wait);
        }
    }

    // A refusal for the limit (with the wait the server named), or a create without a clear answer: the next ask waits, longer
    // each time in a row, and at least as long as the server's Retry-After, kept to MaxNamedWait. Answers the wait; the timer is
    // set apart (ArmRetry).
    private TimeSpan BeginRefusal(DownloadScope scope, TimeSpan? retryAfter, long freed)
    {
        lock (_gate)
        {
            bool sameScope = _refusedScope == scope;
            if (!sameScope)
            {
                _refusals = 0;
            }
            _refusedScope = scope;
            var wait = WaitingRetryDelays[Math.Min(_refusals, WaitingRetryDelays.Count - 1)];
            _refusals++;
            if (retryAfter is { } named && named > wait)
            {
                wait = named < MaxNamedWait ? named : MaxNamedWait;
            }
            _notBefore = _time.GetUtcNow() + wait;
            // A create sent before one refused since counted fewer freed places: those freed before the later refusal did not
            // help it, so the older count must not make them look freed after it.
            _refusedAtFree = sameScope ? Math.Max(_refusedAtFree, freed) : freed;
            return wait;
        }
    }

    private void NoteRefusal(DownloadScope scope, TimeSpan? retryAfter, long freed) => ArmRetry(BeginRefusal(scope, retryAfter, freed));

    // The timer that ends a wait of this length from now; the one before it goes.
    private void ArmRetry(TimeSpan wait)
    {
        lock (_gate)
        {
            ArmRetryLocked(wait);
        }
    }

    private void ArmRetryLocked(TimeSpan wait)
    {
        _retryTimer?.Dispose();
        _timerDue = _time.GetUtcNow() + wait;
        _retryTimer = _time.CreateTimer(static state => ((DownloadManager)state!).OnRetryDue(), this, wait, Timeout.InfiniteTimeSpan);
    }

    private void OnRetryDue()
    {
        lock (_gate)
        {
            _timerDue = null;
        }
        DrainSoon();
    }

    // The line of the profile is empty, or the server took one: no wait holds any more.
    private void ForgetRefusals(DownloadScope scope)
    {
        lock (_gate)
        {
            if (_refusedScope != scope)
            {
                return;
            }
            _refusedScope = null;
            _refusals = 0;
            _retryTimer?.Dispose();
            _retryTimer = null;
            _timerDue = null;
        }
    }

    // The line may be asked now: no refusal holds for the profile, or its wait is over, or a place freed since the refused create
    // was sent. Answers also how many places had freed, to note with the next refusal. While the wait holds, a timer is there for
    // the rest of it: set again when it fired early, as when the clock stepped back.
    private bool MayAsk(DownloadScope scope, out long freed)
    {
        lock (_gate)
        {
            freed = FreedLocked(scope);
            var now = _time.GetUtcNow();
            if (_refusedScope != scope || freed > _refusedAtFree || now >= _notBefore)
            {
                return true;
            }
            if (_timerDue is null)
            {
                ArmRetryLocked(_notBefore - now);
            }
            return false;
        }
    }

    // A store change notes whether it freed a place for the active profile, and starts a run when episodes wait. A place frees
    // on the server, not on this PC: a download still kept here that is no longer preparing, or a delete no longer to send. One
    // deleted here holds its place until its delete is sent. What it compares with is kept while nothing waits too: a place
    // that frees while a season's own create is refused, before anything waits, lets the line that refusal starts be asked at
    // once. The store is read under the manager's lock (the manager's lock, then the store's, never the other way round), so
    // of two changes on two threads the later read is the one the next change compares with. The active profile is read under
    // it too (the session's snapshot, which takes no lock), so a change that looked before a switch never stores its old
    // profile over the new one's. The reads stay inside the lock on purpose: read outside, two changes could store their
    // snapshots in the other order. They are copies of the store's state in memory, one pass over the profile's downloads,
    // which a PC keeps by the hundreds at most; no disk or network is touched.
    private void OnStoreChangedForWaiting(object? sender, EventArgs e)
    {
        if (NoteFreedPlaces())
        {
            DrainSoon();
        }
    }

    // A switch of profile: what a place freed is compared with is the new profile's from now on, so a download of it leaving
    // Preparing before any store change counts; then the line is asked once.
    private void OnSessionChangedForWaiting(object? sender, EventArgs e)
    {
        NoteFreedPlaces();
        DrainSoon();
    }

    // Notes whether the store as it is now freed a place for the active profile since the last look, and keeps what the next
    // look compares with. Answers whether episodes of the profile wait.
    private bool NoteFreedPlaces()
    {
        lock (_gate)
        {
            var scope = DownloadScope.Of(_session);
            if (scope is null)
            {
                (_seenScope, _seenPreparing, _seenDeletes) = (null, [], []);
                return false;
            }
            var downloads = _store.In(scope).ToDictionary(d => d.Record.DownloadId, d => d.Record.State, StringComparer.Ordinal);
            var deletes = _store.PendingDeletes(scope).Select(d => d.DownloadId).ToHashSet(StringComparer.Ordinal);
            if (_seenScope == scope && (_seenPreparing.Any(id => downloads.TryGetValue(id, out var state) && state != DownloadState.Preparing)
                || _seenDeletes.Any(id => !deletes.Contains(id) && !downloads.ContainsKey(id))))
            {
                _freeGenerations[(scope.ServerEntryId, scope.AccountId)] = FreedLocked(scope) + 1;
            }
            var preparing = downloads.Where(d => d.Value == DownloadState.Preparing).Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
            (_seenScope, _seenPreparing, _seenDeletes) = (scope, preparing, deletes);
            return _store.WaitingIn(scope).Count > 0;
        }
    }

    // A run when none runs, else one more look once it ends.
    private void DrainSoon()
    {
        lock (_gate)
        {
            if (_draining)
            {
                _drainAgain = true;
                return;
            }
            _draining = true;
            _drain = Task.Run(DrainAsync);
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            lock (_gate)
            {
                _drainAgain = false;
            }
            try
            {
                await DrainOnceAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The line stays as it is and waits as after a refusal, so a failure is not tried again at every change.
                _logger.LogError("Asking for the waiting episodes again failed unexpectedly ({Failure})", DownloadLog.Failure(ex));
                if (DownloadScope.Of(_session) is { } scope)
                {
                    NoteRefusal(scope, null, FreeGenerationOf(scope));
                }
            }
            lock (_gate)
            {
                if (!_drainAgain)
                {
                    _draining = false;
                    return;
                }
            }
        }
    }

    // The active profile's line, the first first, as far as the server takes it.
    private async Task DrainOnceAsync()
    {
        while (DownloadScope.Of(_session) is { } scope && !_store.IsClearPending(scope))
        {
            var line = _store.WaitingIn(scope);
            if (line.Count == 0)
            {
                ForgetRefusals(scope);
                return;
            }
            if (!MayAsk(scope, out long freed))
            {
                return;
            }
            var next = line[0];
            // Looked at again right before the create: the viewer may have taken it out of line meanwhile.
            if (!_store.IsWaiting(scope, next.EpisodeId))
            {
                continue;
            }
            var outcome = _store.FindByItem(scope, next.EpisodeId) is null
                ? await StartAsync(next.Request, scope, onlyWhileWaiting: true, CancellationToken.None).ConfigureAwait(false)
                : new CreateOutcome(DownloadStartResult.AlreadyDownloading);
            if (DownloadScope.Of(_session) != scope || _store.IsClearPending(scope))
            {
                // Another profile, or a sign-out of this one: the line waits for it, or goes with it.
                return;
            }
            if (!await TakeAsync(scope, next, outcome, freed).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    // What the head's create came to: true when the line goes on to the next one, false when it waits.
    private async Task<bool> TakeAsync(DownloadScope scope, WaitingEpisode next, CreateOutcome outcome, long freed)
    {
        if (outcome.Dropped)
        {
            // Taken out of line while it was asked for: nothing of it is kept, and the line goes on as it is now.
            return true;
        }
        if (outcome.NotActive)
        {
            // Nothing was sent: another profile was active for a moment. The episode stays, and the line waits for its profile.
            return false;
        }
        bool unclear = outcome is { Result: DownloadStartResult.Failed, IsFinal: false };
        int failures;
        lock (_gate)
        {
            failures = unclear ? (_headFailures.EpisodeId == next.EpisodeId ? _headFailures.Count : 0) + 1 : 0;
            _headFailures = unclear ? (next.EpisodeId, failures) : default;
        }
        switch (outcome.Result)
        {
            case DownloadStartResult.Started:
                ForgetRefusals(scope);
                await LeaveLineAsync(scope, next).ConfigureAwait(false);
                return true;
            case DownloadStartResult.AlreadyDownloading when _store.FindByItem(scope, next.EpisodeId) is not null:
                await LeaveLineAsync(scope, next).ConfigureAwait(false);
                return true;
            case DownloadStartResult.AlreadyDownloading:
                // Another start of it is under way, as the viewer's own Download: it stays in line until its download is here,
                // since that start can still fail or meet the limit.
                NoteRefusal(scope, null, freed);
                return false;
            case DownloadStartResult.Unavailable:
                _logger.LogInformation("A waiting episode is not offered to this profile any more; it leaves the line");
                await LeaveLineAsync(scope, next).ConfigureAwait(false);
                return true;
            case DownloadStartResult.Limited:
                _logger.LogInformation("The server's download limit still holds; {Count} episodes wait", _store.WaitingIn(scope).Count);
                NoteRefusal(scope, outcome.RetryAfter, freed);
                return false;
            case DownloadStartResult.Failed when outcome.IsFinal:
                // Asked again, the server would answer the same: the others are not held back by it.
                _logger.LogWarning("The server refused a waiting episode for good; it leaves the line");
                await LeaveLineAsync(scope, next).ConfigureAwait(false);
                return true;
            default:
                if (failures >= HeadTries)
                {
                    _logger.LogInformation("A waiting episode got no clear answer {Count} times in a row; it goes to the back of the line", failures);
                    lock (_gate)
                    {
                        _headFailures = default;
                    }
                    await _store.MoveWaitingToEndAsync(scope, next.EpisodeId, CancellationToken.None).ConfigureAwait(false);
                }
                NoteRefusal(scope, null, freed);
                return false;
        }
    }

    private Task LeaveLineAsync(DownloadScope scope, WaitingEpisode episode) =>
        _store.RemoveWaitingAsync(scope, w => w.EpisodeId == episode.EpisodeId, CancellationToken.None);
}
