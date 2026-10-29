using Granary.Core.Abstractions;
using Granary.Core.Api;
using Granary.Core.Api.Subtitles;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

/// <summary>What the player knows about one syncable subtitle's timing and sync.</summary>
/// <param name="IsBusy">A sync or timing request is on its way.</param>
/// <param name="IsForbidden">The server refused this viewer a retiming (403, as in demo mode).</param>
/// <param name="IsUnsupported">The subtitle's format cannot be synced (422).</param>
/// <param name="Error">The last action's failure, in plain words.</param>
/// <param name="PollExpired">Polling gave up after five minutes; opening the menu again re-arms it.</param>
/// <param name="WatchedJobId">
/// A job this viewer started (a sync they asked for, or the automatic sync of a subtitle they just added): the player
/// reports its progress and outcome to them.
/// </param>
public sealed record SubtitleSyncEntry(SubtitleSyncState State, bool IsBusy = false, bool IsForbidden = false, bool IsUnsupported = false,
    string? Error = null, bool PollExpired = false, string? WatchedJobId = null)
{
    public string Key => State.Key;

    public SubtitleSyncJob? Job => State.Sync;

    public bool IsInProgress => State.Sync?.IsInProgress == true;
}

/// <summary>
/// The timing and latest sync job of the playing file's syncable subtitles (stored ones and files next to the media), by
/// sync key, as Silo's web player (<c>useSubtitleSync</c>) and Apple app (<c>SubtitleSyncModel</c>) keep them: read when
/// the inventory gains or loses one and when the menu opens, running jobs followed through the control lane's events with
/// polling as the fallback, and "Sync to audio" and "Reset timing" carried out. Like the Apple app it follows the file,
/// not the session: another file drops everything in flight, a new session of the same file keeps it.
/// </summary>
/// <remarks>Used on the UI thread; its timer posts there.</remarks>
public sealed class SubtitleSyncTracker : IDisposable
{
    /// <summary>A running job is read again this often while no event came.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>Polling a job gives up after this; opening the menu again re-arms it.</summary>
    public static readonly TimeSpan PollLimit = TimeSpan.FromMinutes(5);

    /// <summary>An event this recent makes the next poll of its subtitle redundant.</summary>
    public static readonly TimeSpan PushFresh = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The server follows a sync result with <c>subtitle_timing_changed</c>: that event this soon after a result that
    /// already reloaded the track reuses the reload.
    /// </summary>
    public static readonly TimeSpan TimingCoalesce = TimeSpan.FromSeconds(5);

    /// <summary>"Sync to audio" and "Reset timing" give up after this, so a request that hangs frees their buttons.</summary>
    public static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(30);

    // Known timing a timing event already reported as changed: the next read records it without a second reload.
    private const string TimingDirty = "dirty";

    // Known timing nobody could read after such an event: the next timing seen reloads the track, to be safe.
    private const string TimingUnknown = "unknown";

    private readonly SubtitleSyncApi _api;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private Dictionary<string, SubtitleSyncEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _knownTiming = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _pollStarted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _pushedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _pushReloadedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _observed = new(StringComparer.Ordinal);
    private HashSet<string> _reading = new(StringComparer.Ordinal);
    private HashSet<string> _rereads = new(StringComparer.Ordinal);
    private long _observation;
    private int _generation;
    private Uri? _server;
    private string? _fileId;
    private string _keys = "";
    private ITimer? _poll;
    // Ends the requests of the file followed when another file is followed, or the tracker is put away.
    private CancellationTokenSource _lifetime = new();
    private bool _listing;
    private bool _listAgain;
    private bool _disposed;

    public SubtitleSyncTracker(SubtitleSyncApi api, IUiDispatcher dispatcher, TimeProvider time, ILogger logger)
    {
        _api = api;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised when an entry or <see cref="IsAvailable"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised with a sync key once per observed change of its timing: the server serves the subtitle's cues anew, so a
    /// track that shows them reads them again.
    /// </summary>
    public event EventHandler<string>? TimingChanged;

    /// <summary>The server aligns subtitles to audio and this viewer may.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>Files next to the media can be synced too.</summary>
    public bool IsExternalAvailable { get; private set; }

    /// <summary>The file followed; null before one and after <see cref="Clear"/>.</summary>
    public string? FileId => _fileId;

    public IReadOnlyDictionary<string, SubtitleSyncEntry> Entries => _entries;

    /// <summary>The entry of <paramref name="syncKey"/>; null for none.</summary>
    public SubtitleSyncEntry? EntryOf(string? syncKey) => syncKey is not null && _entries.TryGetValue(syncKey, out var entry) ? entry : null;

    /// <summary>"Sync to audio" applies to <paramref name="entry"/>: sync is available, its format can be, and a sidecar's too.</summary>
    public bool CanSync(SubtitleSyncEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return IsAvailable && !entry.IsUnsupported && (!entry.State.IsExternal || IsExternalAvailable);
    }

    /// <summary>
    /// Follows <paramref name="fileId"/> on <paramref name="server"/> whose inventory names <paramref name="syncKeys"/>.
    /// Another file drops everything and asks the server again what it supports; a changed set of keys reads the list again.
    /// </summary>
    public void Follow(Uri server, string fileId, IEnumerable<string> syncKeys)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        ArgumentNullException.ThrowIfNull(syncKeys);
        if (_disposed)
        {
            return;
        }
        string keys = string.Join(",", syncKeys.Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        if (server != _server || fileId != _fileId)
        {
            Reset();
            _server = server;
            _fileId = fileId;
            _keys = keys;
            Start(ProbeAsync(_generation));
            return;
        }
        if (keys != _keys)
        {
            _keys = keys;
            List();
        }
    }

    /// <summary>Forgets the file: playback ended.</summary>
    public void Clear()
    {
        Reset();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads the file's subtitles again and re-arms expired polls, so a job that ended meanwhile shows (the menu opened).</summary>
    public void Reload()
    {
        if (_fileId is null)
        {
            return;
        }
        _pollStarted.Clear();
        _entries = _entries.ToDictionary(e => e.Key, e => e.Value with { PollExpired = false }, StringComparer.Ordinal);
        if (!IsAvailable)
        {
            // The first probe may have failed: a menu opened later asks again.
            Start(ProbeAsync(_generation));
            return;
        }
        List(force: true);
        UpdatePolling();
    }

    /// <summary>Records a subtitle the viewer just added, following its automatic sync.</summary>
    public void Remember(StoredSubtitle subtitle)
    {
        ArgumentNullException.ThrowIfNull(subtitle);
        if (_fileId is null || subtitle.MediaFileId != _fileId || subtitle.Id.Length == 0)
        {
            return;
        }
        var state = subtitle.SyncState();
        _pollStarted.Remove(state.Key);
        Observe(state, watch: state.Sync is { IsInProgress: true } job ? job.Id : null);
    }

    /// <summary>The server announced new timing for <paramref name="key"/> (control lane): the track reloads, the state is read again.</summary>
    public void OnTimingChanged(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_fileId is null)
        {
            return;
        }
        bool reloadedJustNow = _pushReloadedAt.Remove(key, out var reloadedAt) && _time.GetUtcNow() - reloadedAt < TimingCoalesce;
        // Right after a pushed result reloaded the track, this event is about that change; the read below still reloads
        // it if the timing differs from the one just loaded.
        if (!reloadedJustNow)
        {
            _knownTiming[key] = TimingDirty;
            TimingChanged?.Invoke(this, key);
        }
        Start(ReadOneAsync(key));
    }

    /// <summary>The server reported a step of <paramref name="key"/>'s sync job (control lane).</summary>
    public void OnSyncUpdated(string key, SubtitleTiming timing, SubtitleSyncJob job)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentNullException.ThrowIfNull(job);
        if (_fileId is null)
        {
            return;
        }
        if (!_entries.TryGetValue(key, out var entry))
        {
            // Not read yet (a track the inventory just gained): read them all.
            List(force: true);
            return;
        }
        if (entry.State.Sync is { } known && IsOutdated(job, known))
        {
            return;
        }
        _pushedAt[key] = _time.GetUtcNow();
        Observe(entry.State with { Timing = timing, Sync = job }, pushed: true);
    }

    /// <summary>"Sync to audio": starts a job, or joins the running one, and follows it. Not while one is asked for or runs.</summary>
    public async Task RequestSyncAsync(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_server is not { } server || _fileId is not { } fileId || EntryOf(key) is not { IsBusy: false, IsInProgress: false })
        {
            return;
        }
        int generation = _generation;
        Patch(key, e => e with { IsBusy = true, Error = null, PollExpired = false });
        _pollStarted.Remove(key);
        long sentAt = _observation;
        using var limit = new CancellationTokenSource(ActionTimeout, _time);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, limit.Token);
        var (state, failure) = await AnswerOf(_api.StartAsync(server, fileId, key, request.Token)).ConfigureAwait(false);
        await _dispatcher.ResumeOnUi();
        // An answer for a file no longer followed is nobody's: a command that waits for it ends quietly.
        if (generation != _generation)
        {
            return;
        }
        if (failure is not null)
        {
            Refused(key, failure, "Sync failed");
            return;
        }
        Observe(state!, watch: state!.Sync?.Id, sentAt: sentAt);
        Patch(key, e => e with { IsBusy = false });
    }

    /// <summary>"Reset timing": the original timing again, written at the version just read. Not while a request or a job runs.</summary>
    public async Task ResetTimingAsync(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_server is not { } server || _fileId is not { } fileId || EntryOf(key) is not { IsBusy: false, IsInProgress: false })
        {
            return;
        }
        int generation = _generation;
        Patch(key, e => e with { IsBusy = true, Error = null });
        using var limit = new CancellationTokenSource(ActionTimeout, _time);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, limit.Token);
        var (read, readFailure) = await AnswerOf(_api.ReadAsync(server, fileId, key, request.Token)).ConfigureAwait(false);
        await _dispatcher.ResumeOnUi();
        if (generation != _generation)
        {
            return;
        }
        if (readFailure is not null)
        {
            Refused(key, readFailure, "Couldn't reset timing");
            return;
        }
        long sentAt = _observation;
        var (state, failure) = await AnswerOf(_api.SetTimingAsync(server, fileId, key, read!.Version, SubtitleTiming.Identity, request.Token))
            .ConfigureAwait(false);
        await _dispatcher.ResumeOnUi();
        if (generation != _generation)
        {
            return;
        }
        if (failure is not null)
        {
            Refused(key, failure, "Couldn't reset timing");
            return;
        }
        Observe(state!, sentAt: sentAt);
        Patch(key, e => e with { IsBusy = false });
    }

    public void Dispose()
    {
        _disposed = true;
        Reset();
        _lifetime.Dispose();
    }

    // A server call's answer or its failure, never thrown: the caller takes either on the UI thread (ResumeOnUi), whatever
    // thread the answer completed on, so nothing a command or a read nobody awaits does can escape it.
    private static async Task<(T? Value, Exception? Failure)> AnswerOf<T>(Task<T> call)
    {
        try
        {
            return (await call.ConfigureAwait(false), null);
        }
        catch (Exception ex)
        {
            return (default, ex);
        }
    }

    // The web words a refusal by its status; anything else by what it was asked to do. A subtitle that changed meanwhile
    // (412) is read again, so what shows, and the buttons, are its current state. A failure that is no refusal at all is a
    // fault of this app's, logged as one.
    private void Refused(string key, Exception ex, string fallback)
    {
        if (LibraryErrors.IsExpected(ex))
        {
            _logger.LogWarning("A subtitle sync request was refused: {Failure}", ApiFailureText.Describe(ex));
        }
        else
        {
            _logger.LogError(ex, "A subtitle sync request failed");
        }
        if (ex is SiloApiException { Status: 412 })
        {
            Start(ReadOneAsync(key));
        }
        Patch(key, e => ex is SiloApiException api ? api.Status switch
        {
            403 => e with { IsBusy = false, IsForbidden = true, Error = null },
            422 => e with { IsBusy = false, IsUnsupported = true, Error = "This format can't be synced." },
            412 => e with { IsBusy = false, Error = "The subtitle changed. Try again." },
            _ => e with { IsBusy = false, Error = fallback },
        } : e with { IsBusy = false, Error = fallback });
    }

    private void Reset()
    {
        _generation++;
        _lifetime.Cancel();
        if (!_disposed)
        {
            _lifetime.Dispose();
            _lifetime = new CancellationTokenSource();
        }
        _poll?.Dispose();
        _poll = null;
        _server = null;
        _fileId = null;
        _keys = "";
        IsAvailable = false;
        IsExternalAvailable = false;
        _entries = new(StringComparer.Ordinal);
        _knownTiming.Clear();
        _pollStarted.Clear();
        _pushedAt.Clear();
        _pushReloadedAt.Clear();
        _observed.Clear();
        // New sets: a read of the old file releases on its own sets, never on the new file's.
        _reading = new(StringComparer.Ordinal);
        _rereads = new(StringComparer.Ordinal);
        _listing = false;
        _listAgain = false;
    }

    private async Task ProbeAsync(int generation)
    {
        if (_server is not { } server)
        {
            return;
        }
        var (status, failure) = await AnswerOf(_api.GetStatusAsync(server, _lifetime.Token)).ConfigureAwait(false);
        await _dispatcher.ResumeOnUi();
        if (generation != _generation)
        {
            return;
        }
        if (failure is not null)
        {
            // Sync is decoration: the tracks play without it.
            LogReadFailure("Subtitle sync status could not be read", failure);
            return;
        }
        IsAvailable = status!.IsAvailable;
        IsExternalAvailable = status.IsAvailable && status.External;
        Changed?.Invoke(this, EventArgs.Empty);
        if (IsAvailable)
        {
            List(force: true);
        }
    }

    // Work nobody awaits: whatever it throws is logged, never left unobserved.
    private void Start(Task work) => _ = work.ContinueWith(task => _logger.LogError(task.Exception, "Subtitle sync failed"), CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    // A read that failed: the server's refusal or an unreachable server is expected, anything else a fault of this app's.
    private void LogReadFailure(string what, Exception failure)
    {
        if (LibraryErrors.IsExpected(failure))
        {
            _logger.LogInformation("{What}: {Failure}", what, ApiFailureText.Describe(failure));
        }
        else
        {
            _logger.LogError(failure, "{What}", what);
        }
    }

    // The file's syncable subtitles, read whenever the inventory gains or loses one. An inventory without any still reads
    // while some are known, so the server says which are gone. One listing at a time; one asked for meanwhile follows it.
    private void List(bool force = false)
    {
        if (!IsAvailable || _fileId is null || (!force && _keys.Length == 0 && _entries.Count == 0))
        {
            return;
        }
        if (_listing)
        {
            _listAgain = true;
            return;
        }
        Start(ListAsync(_generation));
    }

    private async Task ListAsync(int generation)
    {
        _listing = true;
        try
        {
            do
            {
                _listAgain = false;
                long sentAt = _observation;
                var (states, failure) = await AnswerOf(_api.ListAsync(_server!, _fileId!, _lifetime.Token)).ConfigureAwait(false);
                await _dispatcher.ResumeOnUi();
                if (generation != _generation)
                {
                    return;
                }
                if (failure is not null)
                {
                    LogReadFailure("Subtitle sync states could not be read", failure);
                    return;
                }
                foreach (var state in states!)
                {
                    Observe(state, sentAt: sentAt, notify: false);
                }
                // The list names every subtitle of the file that can be synced.
                var listed = states.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
                foreach (string key in _entries.Keys.Where(k => !listed.Contains(k)).ToList())
                {
                    Forget(key, sentAt, notify: false);
                }
                Changed?.Invoke(this, EventArgs.Empty);
                UpdatePolling();
            }
            while (_listAgain && generation == _generation);
        }
        finally
        {
            if (generation == _generation)
            {
                _listing = false;
            }
        }
    }

    // One subtitle read again; a read asked for while one is on its way follows it, as that one may answer from before.
    private async Task ReadOneAsync(string key)
    {
        if (_server is not { } server || _fileId is not { } fileId)
        {
            return;
        }
        if (_reading.Contains(key))
        {
            _rereads.Add(key);
            return;
        }
        int generation = _generation;
        var reading = _reading;
        var rereads = _rereads;
        reading.Add(key);
        try
        {
            do
            {
                long sentAt = _observation;
                var (read, failure) = await AnswerOf(_api.ReadAsync(server, fileId, key, _lifetime.Token)).ConfigureAwait(false);
                await _dispatcher.ResumeOnUi();
                if (generation != _generation)
                {
                    return;
                }
                if (failure is SiloApiException { Status: 404 })
                {
                    // The subtitle is gone, and its jobs with it.
                    Forget(key, sentAt);
                    return;
                }
                if (failure is not null)
                {
                    // Any other failure stops polling; opening the menu re-arms it. A timing event's reload was taken without
                    // knowing the timing it loaded: the next timing seen reloads again rather than none.
                    LogReadFailure("A subtitle's sync state could not be read", failure);
                    if (_knownTiming.TryGetValue(key, out string? known) && known == TimingDirty)
                    {
                        _knownTiming[key] = TimingUnknown;
                    }
                    Patch(key, e => e with { PollExpired = true });
                    return;
                }
                Observe(read!.Value, sentAt: sentAt);
            }
            while (rereads.Remove(key) && generation == _generation);
        }
        finally
        {
            reading.Remove(key);
            rereads.Remove(key);
        }
    }

    // Records a fresh view of a subtitle and reports a timing change. A read sent before a newer observation (an event
    // that arrived while it was on its way) is older than that and dropped. A pushed result that reloads the track spares
    // the timing event that follows it a second reload.
    private void Observe(SubtitleSyncState state, string? watch = null, long? sentAt = null, bool pushed = false, bool notify = true)
    {
        string key = state.Key;
        if (key.Length == 0)
        {
            return;
        }
        if (sentAt is { } sent && _observed.TryGetValue(key, out long seen) && seen > sent)
        {
            if (watch is not null)
            {
                Patch(key, e => e with { WatchedJobId = watch }, notify);
            }
            return;
        }
        _observed[key] = ++_observation;
        string timing = TimingKey(state.Timing);
        bool hadTiming = _knownTiming.TryGetValue(key, out string? previous);
        _knownTiming[key] = timing;
        bool inProgress = state.Sync?.IsInProgress == true;
        if (!inProgress)
        {
            _pollStarted.Remove(key);
        }
        _entries.TryGetValue(key, out var before);
        _entries[key] = (before ?? new SubtitleSyncEntry(state)) with
        {
            State = state,
            PollExpired = inProgress && before?.PollExpired == true,
            WatchedJobId = watch ?? before?.WatchedJobId,
        };
        if (notify)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            UpdatePolling();
        }
        if (hadTiming && previous != TimingDirty && previous != timing)
        {
            if (pushed)
            {
                _pushReloadedAt[key] = _time.GetUtcNow();
            }
            TimingChanged?.Invoke(this, key);
        }
    }

    // Forgets a subtitle the server no longer has, so the progress of a job that will never end does not stay. Counts as an
    // observation: an older answer cannot bring it back.
    private void Forget(string key, long sentAt, bool notify = true)
    {
        if (_observed.TryGetValue(key, out long seen) && seen > sentAt)
        {
            return;
        }
        _observed[key] = ++_observation;
        _knownTiming.Remove(key);
        _pollStarted.Remove(key);
        _pushReloadedAt.Remove(key);
        if (_entries.Remove(key) && notify)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            UpdatePolling();
        }
    }

    private void Patch(string key, Func<SubtitleSyncEntry, SubtitleSyncEntry> change, bool notify = true)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            return;
        }
        _entries[key] = change(entry);
        if (notify)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            UpdatePolling();
        }
    }

    // A running job is polled every few seconds until it ends, the limit passes, or the file changes. Events usually come
    // first; a subtitle that had one recently skips the poll.
    private void UpdatePolling()
    {
        var polled = _entries.Values.Where(e => e.IsInProgress && !e.PollExpired).ToList();
        bool any = polled.Count > 0;
        // A job's five minutes start when its polling begins.
        var now = _time.GetUtcNow();
        foreach (var entry in polled)
        {
            _pollStarted.TryAdd(entry.Key, now);
        }
        if (any && _poll is null)
        {
            int generation = _generation;
            _poll = _time.CreateTimer(_ => _dispatcher.Post(() => Tick(generation)), null, PollInterval, PollInterval);
        }
        else if (!any && _poll is not null)
        {
            _poll.Dispose();
            _poll = null;
        }
    }

    private void Tick(int generation)
    {
        if (_disposed || generation != _generation)
        {
            return;
        }
        var now = _time.GetUtcNow();
        foreach (var entry in _entries.Values.Where(e => e.IsInProgress && !e.PollExpired).ToList())
        {
            string key = entry.Key;
            // Polling began the clock; Remember, Reload and a new request start it again.
            if (!_pollStarted.TryGetValue(key, out var started))
            {
                started = now;
                _pollStarted[key] = started;
            }
            if (now - started >= PollLimit)
            {
                _pollStarted.Remove(key);
                Patch(key, e => e with { PollExpired = true });
            }
            else if (!_pushedAt.TryGetValue(key, out var pushed) || now - pushed >= PushFresh)
            {
                Start(ReadOneAsync(key));
            }
        }
    }

    private static string TimingKey(SubtitleTiming timing) =>
        timing.OffsetMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + timing.Scale.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    // A pushed job older than the known one: created before it, or the same job at an earlier stage (queued after it ran,
    // running after it ended). Events from different servers, or sent off the sync worker, can arrive out of order.
    private static bool IsOutdated(SubtitleSyncJob job, SubtitleSyncJob known) =>
        job.Id != known.Id ? string.CompareOrdinal(job.CreatedAt, known.CreatedAt) < 0 : Stage(job.Status) < Stage(known.Status);

    private static int Stage(string status) => status switch
    {
        SubtitleSyncStatuses.Pending => 0,
        SubtitleSyncStatuses.Running => 1,
        _ => 2,
    };
}
