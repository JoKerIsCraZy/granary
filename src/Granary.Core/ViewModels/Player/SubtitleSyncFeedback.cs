using Granary.Core.Abstractions;
using Granary.Core.Api.Subtitles;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels.Player;

/// <summary>How the sync indicator looks: a running job, a success, a note, or a warning.</summary>
public enum SubtitleSyncTone
{
    Progress,
    Success,
    Info,
    Warning,
}

/// <summary>What the player's sync indicator shows (the web's <c>SubtitleSyncIndicator</c>).</summary>
/// <param name="Id">Stays the same while one job runs, so the indicator updates in place.</param>
/// <param name="Percent">0 to 100 while a job runs.</param>
public sealed record SubtitleSyncNotice(string Id, SubtitleSyncTone Tone, string Title, string? Detail = null, int? Percent = null)
{
    public bool IsRunning => Tone == SubtitleSyncTone.Progress;
}

/// <summary>The latest job this viewer started, with its subtitle's name ("German") and timing.</summary>
/// <param name="PollExpired">Following the job gave up while it still ran; opening the subtitle menu follows it again.</param>
public sealed record WatchedSubtitleSync(string Key, string Name, SubtitleSyncJob Job, SubtitleTiming Timing, bool PollExpired = false);

/// <summary>
/// Turns subtitle sync into one notice at a time, as Silo's web player does (<c>useSubtitleSyncFeedback</c>): the progress
/// and then the outcome of a sync this viewer started; when it changed the subtitle that shows, "Applying new timing…"
/// until its corrected cues show; and a note when someone else's change to the subtitle that shows reaches the screen.
/// Finished notices leave on their own.
/// </summary>
/// <remarks>Used on the UI thread; its timers post there.</remarks>
public sealed class SubtitleSyncFeedback : IDisposable
{
    public static readonly TimeSpan NoticeVisible = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan WarningVisible = TimeSpan.FromSeconds(9);

    /// <summary>How long "Applying new timing…" waits for the reloaded cues before it says done anyway (a burned-in track never reloads).</summary>
    public static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(8);

    private readonly TimeProvider _time;
    private readonly IUiDispatcher _dispatcher;

    // Each key's cue revision grows with every change of its timing; a key's loaded revision is the one whose cues show.
    private readonly Dictionary<string, int> _revisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _startRevision = new(StringComparer.Ordinal);
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private readonly HashSet<string> _applied = new(StringComparer.Ordinal);
    private Input? _input;
    private Applying? _applying;
    private (string Key, int Revision)? _foreign;
    private WatchedSubtitleSync? _watched;
    private string? _activeKey;
    private SubtitleSyncEntry? _active;
    private ITimer? _noticeTimer;
    private ITimer? _applyTimer;

    public SubtitleSyncFeedback(TimeProvider time, IUiDispatcher dispatcher)
    {
        _time = time;
        _dispatcher = dispatcher;
    }

    public SubtitleSyncNotice? Notice { get; private set; }

    public event EventHandler? NoticeChanged;

    /// <summary>What the sync state and the screen are now: the watched job, and the subtitle that shows with its entry.</summary>
    public void Update(WatchedSubtitleSync? watched, string? activeKey, SubtitleSyncEntry? active)
    {
        _watched = watched;
        _activeKey = activeKey;
        _active = active;
        Step();
    }

    /// <summary>The timing of <paramref name="key"/> changed: its cues are about to be read again.</summary>
    public void TimingChanged(string key)
    {
        _revisions[key] = Revision(key) + 1;
        Step();
    }

    /// <summary>The corrected cues of <paramref name="key"/> show.</summary>
    public void CuesShown(string key)
    {
        _loaded[key] = Revision(key);
        Step();
    }

    public void Dismiss() => Show(null);

    /// <summary>Another file: nothing of the old one is announced.</summary>
    public void Reset()
    {
        _revisions.Clear();
        _loaded.Clear();
        _startRevision.Clear();
        _announced.Clear();
        _applied.Clear();
        _input = null;
        _foreign = null;
        _watched = null;
        _activeKey = null;
        _active = null;
        SetApplying(null);
        Show(null);
    }

    public void Dispose()
    {
        _noticeTimer?.Dispose();
        _applyTimer?.Dispose();
    }

    private int Revision(string? key) => key is not null && _revisions.TryGetValue(key, out int revision) ? revision : 0;

    private bool LoadedAfter(string key, int revision) => (_loaded.TryGetValue(key, out int loaded) ? loaded : -1) > revision;

    private void Step()
    {
        var before = _input;
        var input = new Input(_activeKey, Revision(_activeKey));
        _input = input;

        if (_watched is { } watched)
        {
            var job = watched.Job;
            _startRevision.TryAdd(job.Id, Revision(watched.Key));
            if (job.IsInProgress && watched.PollExpired)
            {
                // Nobody follows the job any more: its spinner would never end. A note says so once, and leaves.
                if (_announced.Add(job.Id + ":unfollowed"))
                {
                    Show(new SubtitleSyncNotice(job.Id + ":unfollowed", SubtitleSyncTone.Info, $"Still syncing {watched.Name} subtitles",
                        "Open the subtitle menu later to see how it ended."));
                }
                else if (Notice is { IsRunning: true } running && running.Id == job.Id)
                {
                    Show(null);
                }
            }
            else if (job.IsInProgress)
            {
                Show(new SubtitleSyncNotice(job.Id, SubtitleSyncTone.Progress, $"Syncing {watched.Name} subtitles", SubtitleSyncText.Phase(job),
                    SubtitleSyncText.Percent(job) ?? 0));
            }
            else if (_announced.Add(job.Id))
            {
                if (job.Status == SubtitleSyncStatuses.Synced && watched.Key == input.ActiveKey)
                {
                    SetApplying(new Applying(watched.Key, job.Id, SubtitleSyncText.Timing(watched.Timing), _startRevision[job.Id]));
                    Show(new SubtitleSyncNotice(job.Id, SubtitleSyncTone.Progress, "Applying new timing…", Percent: 100));
                }
                else
                {
                    Show(Outcome(watched));
                }
            }
        }
        else
        {
            // The watched job is gone with the file it belonged to: nothing will finish its progress.
            SetApplying(null);
            if (Notice?.IsRunning == true)
            {
                Show(null);
            }
        }

        if (_applying is { } applying && (applying.Key != input.ActiveKey || LoadedAfter(applying.Key, applying.StartRevision)))
        {
            FinishApplying();
        }

        if (input.ActiveKey is { } activeKey && before?.ActiveKey == activeKey && input.ActiveRevision > before.ActiveRevision)
        {
            var activeJob = _active?.State.Sync;
            bool own = _active?.WatchedJobId is { } watchedId && activeJob?.Id == watchedId
                && (activeJob.IsInProgress || (activeJob.Status == SubtitleSyncStatuses.Synced && !_applied.Contains(activeJob.Id)));
            // A subtitle synced automatically the first time anyone played it changes without a word: it just lines up.
            bool automatic = activeJob is { Trigger: "auto", Status: SubtitleSyncStatuses.Synced } && _active!.State.Timing.SameAs(activeJob.Result);
            if (!own && !automatic)
            {
                _foreign = (activeKey, before.ActiveRevision);
            }
        }
        if (_foreign is { } foreign)
        {
            if (foreign.Key != input.ActiveKey)
            {
                _foreign = null;
            }
            else if (LoadedAfter(foreign.Key, foreign.Revision))
            {
                _foreign = null;
                Show(new SubtitleSyncNotice($"timing:{foreign.Key}:{input.ActiveRevision}", SubtitleSyncTone.Info, "Subtitle timing updated"));
            }
        }
    }

    private static SubtitleSyncNotice? Outcome(WatchedSubtitleSync watched)
    {
        var job = watched.Job;
        string name = watched.Name;
        return job.Status switch
        {
            SubtitleSyncStatuses.Synced => new(job.Id, SubtitleSyncTone.Success, $"{name} subtitles synced", SubtitleSyncText.Timing(watched.Timing)),
            SubtitleSyncStatuses.AlreadySynced => new(job.Id, SubtitleSyncTone.Info, $"{name} subtitles already match the audio"),
            SubtitleSyncStatuses.NoMatch => new(job.Id, SubtitleSyncTone.Warning, $"{name} subtitles don't match the audio",
                "They're probably for another release. The timing wasn't changed."),
            SubtitleSyncStatuses.Failed => new(job.Id, SubtitleSyncTone.Warning, $"Couldn't sync {name} subtitles", SubtitleSyncText.Failure(job.Failure)),
            _ => null,
        };
    }

    // The success the applying job earned, once its cues show (or the wait for them ended).
    private void FinishApplying()
    {
        if (_applying is not { } applying)
        {
            return;
        }
        SetApplying(null);
        _applied.Add(applying.JobId);
        Show(new SubtitleSyncNotice(applying.JobId, SubtitleSyncTone.Success, "Subtitles synced", applying.Detail));
    }

    private void SetApplying(Applying? applying)
    {
        if (applying == _applying)
        {
            return;
        }
        _applying = applying;
        _applyTimer?.Dispose();
        _applyTimer = null;
        if (applying is not null)
        {
            _applyTimer = _time.CreateTimer(_ => _dispatcher.Post(() =>
            {
                if (_applying == applying)
                {
                    FinishApplying();
                }
            }), null, ApplyTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    private void Show(SubtitleSyncNotice? notice)
    {
        if (notice == Notice)
        {
            return;
        }
        Notice = notice;
        _noticeTimer?.Dispose();
        _noticeTimer = null;
        if (notice is { IsRunning: false })
        {
            var visible = notice.Tone == SubtitleSyncTone.Warning ? WarningVisible : NoticeVisible;
            _noticeTimer = _time.CreateTimer(_ => _dispatcher.Post(() =>
            {
                if (ReferenceEquals(Notice, notice))
                {
                    Show(null);
                }
            }), null, visible, Timeout.InfiniteTimeSpan);
        }
        NoticeChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed record Input(string? ActiveKey, int ActiveRevision);

    private sealed record Applying(string Key, string JobId, string Detail, int StartRevision);
}
