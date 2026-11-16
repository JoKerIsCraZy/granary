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
/// one), and then as far as the server takes them. A refusal waits longer each time; a create that got no clear answer, or a
/// run that failed, waits as one. At a start, or a switch to the profile, the line is asked once, then waits.
/// </summary>
/// <remarks>
/// One run at a time (<see cref="WaitingRun"/>); a trigger while one runs has it look once more. The state below is guarded by
/// the manager's <c>_gate</c>; no store write, create or timer callback runs while it is held. The store's handlers only note
/// and start a run: what the run does happens on the thread pool.
/// </remarks>
public sealed partial class DownloadManager
{
    /// <summary>The waits after a refusal for the limit, one after another; the last repeats.</summary>
    public static readonly IReadOnlyList<TimeSpan> WaitingRetryDelays =
        [.. new[] { 1, 2, 5, 10, 30 }.Select(minutes => TimeSpan.FromMinutes(minutes))];

    private bool _draining;
    private bool _drainAgain;
    private Task _drain = Task.CompletedTask;

    // The profile whose create the limit refused last, how often in a row, the earliest the line is asked again, and whether one
    // of its downloads left its preparation, or had its delete reach the server, since.
    private DownloadScope? _refusedScope;
    private int _refusals;
    private DateTimeOffset _notBefore;
    private bool _freed;
    private ITimer? _retryTimer;

    // The active profile's downloads as the last store change left them: the ones the server prepares, and the deletes still to
    // reach it.
    private DownloadScope? _seenScope;
    private HashSet<string> _seenPreparing = [];
    private HashSet<string> _seenDeletes = [];

    // The Retry-After of the last refusal for the limit, if the server named one.
    private TimeSpan? _limitRetryAfter;

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

    /// <summary>
    /// How many of the active profile's episodes wait for the server's limit of downloads; 0 without a profile.
    /// </summary>
    public int WaitingCount() => DownloadScope.Of(_session) is { } scope ? _store.WaitingIn(scope).Count : 0;

    /// <summary>The active profile's episode <paramref name="mediaItemId"/> waits for the server's limit: it is on its way here.</summary>
    public bool IsWaiting(string mediaItemId) => DownloadScope.Of(_session) is { } scope && _store.IsWaiting(scope, mediaItemId);

    /// <summary>
    /// Takes the waiting episodes of <paramref name="seriesId"/> (every series when null) out of <paramref name="scope"/>'s line:
    /// the viewer cancelled them, or deleted the series' downloads.
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
    // again before it, unless a download leaves its preparation or its delete reaches the server. Answers how many wait.
    private async Task<int> KeepWaitingAsync(DownloadScope scope, string seriesId, string seriesTitle, string quality,
        IEnumerable<(int Season, SeasonEpisode Episode)> episodes, long clears)
    {
        NoteRefusal(scope, false);
        List<WaitingEpisode> line = [.. episodes.Select(e => new WaitingEpisode(scope, seriesId, seriesTitle, e.Season, e.Episode.EpisodeId,
            e.Episode.EpisodeNumber, e.Episode.Title, e.Episode.MediaFileId, quality))];
        try
        {
            return await _store.AddWaitingUnlessClearedAsync(line, clears, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The episodes past the server's download limit could not be kept waiting ({Failure})", DownloadLog.Failure(ex));
            return 0;
        }
    }

    // A refusal for the limit, or a create without a clear answer: the next ask waits, longer each time in a row, and at least as
    // long as the server's Retry-After.
    private void NoteRefusal(DownloadScope scope, bool uncertain)
    {
        lock (_gate)
        {
            if (_refusedScope != scope)
            {
                _refusals = 0;
            }
            _refusedScope = scope;
            var wait = WaitingRetryDelays[Math.Min(_refusals, WaitingRetryDelays.Count - 1)];
            _refusals++;
            if (!uncertain && _limitRetryAfter is { } named && named > wait)
            {
                wait = named;
            }
            _limitRetryAfter = null;
            _notBefore = _time.GetUtcNow() + wait;
            _freed = false;
            _retryTimer?.Dispose();
            _retryTimer = _time.CreateTimer(static state => ((DownloadManager)state!).DrainSoon(), this, wait, Timeout.InfiniteTimeSpan);
        }
    }

    private void NoteLimit(TimeSpan? retryAfter)
    {
        lock (_gate)
        {
            _limitRetryAfter = retryAfter;
        }
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
        }
    }

    // The line may be asked now: no refusal holds for the profile, or its wait is over, or a download left its preparation or
    // had its delete reach the server since.
    private bool MayAsk(DownloadScope scope)
    {
        lock (_gate)
        {
            if (_refusedScope != scope || _freed || _time.GetUtcNow() >= _notBefore)
            {
                _freed = false;
                return true;
            }
            return false;
        }
    }

    // A store change only notes whether it freed a place for the active profile, and starts a run when episodes wait. A place
    // frees on the server, not on this PC: a download still kept here that is no longer preparing, or a delete no longer to
    // send. One deleted here holds its place until its delete is sent.
    private void OnStoreChangedForWaiting(object? sender, EventArgs e)
    {
        var scope = DownloadScope.Of(_session);
        if (scope is null)
        {
            return;
        }
        var downloads = _store.In(scope).ToDictionary(d => d.Record.DownloadId, d => d.Record.State, StringComparer.Ordinal);
        var preparing = downloads.Where(d => d.Value == DownloadState.Preparing).Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var deletes = _store.PendingDeletes(scope).Select(d => d.DownloadId).ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            if (_seenScope == scope && (_seenPreparing.Any(id => downloads.TryGetValue(id, out var state) && state != DownloadState.Preparing)
                || _seenDeletes.Any(id => !deletes.Contains(id) && !downloads.ContainsKey(id))))
            {
                _freed = true;
            }
            (_seenScope, _seenPreparing, _seenDeletes) = (scope, preparing, deletes);
        }
        if (_store.WaitingIn(scope).Count > 0)
        {
            DrainSoon();
        }
    }

    private void OnSessionChangedForWaiting(object? sender, EventArgs e) => DrainSoon();

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
                    NoteRefusal(scope, true);
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
            if (!MayAsk(scope))
            {
                return;
            }
            var next = line[0];
            var result = _store.FindByItem(scope, next.EpisodeId) is null
                ? await StartAsync(next.Request, scope, CancellationToken.None).ConfigureAwait(false)
                : DownloadStartResult.AlreadyDownloading;
            if (DownloadScope.Of(_session) != scope || _store.IsClearPending(scope))
            {
                // Another profile, or a sign-out of this one: the line waits for it, or goes with it.
                return;
            }
            switch (result)
            {
                case DownloadStartResult.Started or DownloadStartResult.AlreadyDownloading:
                    ForgetRefusals(scope);
                    await _store.RemoveWaitingAsync(scope, w => w.EpisodeId == next.EpisodeId, CancellationToken.None).ConfigureAwait(false);
                    break;
                case DownloadStartResult.Unavailable:
                    _logger.LogInformation("A waiting episode is not offered to this profile any more; it leaves the line");
                    await _store.RemoveWaitingAsync(scope, w => w.EpisodeId == next.EpisodeId, CancellationToken.None).ConfigureAwait(false);
                    break;
                case DownloadStartResult.Limited:
                    _logger.LogInformation("The server's download limit still holds; {Count} episodes wait", line.Count);
                    NoteRefusal(scope, false);
                    return;
                default:
                    NoteRefusal(scope, true);
                    return;
            }
        }
    }
}
