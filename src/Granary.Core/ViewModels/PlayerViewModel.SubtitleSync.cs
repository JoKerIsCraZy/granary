using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Subtitles;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Player;

namespace Granary.Core.ViewModels;

// Subtitle timing and sync, as Silo's web player shows them: each track's timing under its row, the TIMING section for the
// subtitle that shows ("Sync to audio", "Reset timing"), and the indicator top right that follows a sync this viewer started.
public sealed partial class PlayerViewModel
{
    private SubtitleSyncFeedback? _syncFeedback;
    private SubtitleSyncTracker? _syncTracker;

    /// <summary>The TIMING section of the subtitle menu, for the subtitle that shows.</summary>
    [ObservableProperty]
    public partial SubtitleTimingPanel SubtitleTiming { get; private set; } = SubtitleTimingPanel.Hidden;

    /// <summary>What the sync indicator shows; null while it is away.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSyncNotice), nameof(SyncNoticeTitle), nameof(SyncNoticeDetail), nameof(HasSyncNoticeDetail),
        nameof(SyncNoticeTone), nameof(IsSyncNoticeRunning), nameof(SyncNoticePercent), nameof(SyncNoticePercentValue), nameof(SyncNoticePercentText))]
    public partial SubtitleSyncNotice? SyncNotice { get; private set; }

    public bool HasSyncNotice => SyncNotice is not null;

    public string SyncNoticeTitle => SyncNotice?.Title ?? "";

    public string SyncNoticeDetail => SyncNotice?.Detail ?? "";

    public bool HasSyncNoticeDetail => SyncNoticeDetail.Length > 0;

    public SubtitleSyncTone SyncNoticeTone => SyncNotice?.Tone ?? SubtitleSyncTone.Info;

    public bool IsSyncNoticeRunning => SyncNotice?.IsRunning == true;

    public int SyncNoticePercent => SyncNotice?.Percent ?? 0;

    /// <summary><see cref="SyncNoticePercent"/> for a progress bar.</summary>
    public double SyncNoticePercentValue => SyncNoticePercent;

    public string SyncNoticePercentText => $"{SyncNoticePercent}%";

    /// <summary>The subtitle menu opened: a job that ended meanwhile shows, and expired polls start again.</summary>
    public void SubtitleMenuOpened() => _syncTracker?.Reload();

    /// <summary>Add Subtitles stored <paramref name="subtitle"/>: its automatic sync is followed, and the indicator reports it.</summary>
    public void RememberAddedSubtitle(StoredSubtitle subtitle)
    {
        ArgumentNullException.ThrowIfNull(subtitle);
        _syncTracker?.Remember(subtitle);
    }

    [RelayCommand]
    private Task SyncSubtitleToAudioAsync() =>
        _syncTracker is { } sync && _playback?.SubtitleSyncKey is { } key ? sync.RequestSyncAsync(key) : Task.CompletedTask;

    [RelayCommand]
    private Task ResetSubtitleTimingAsync() =>
        _syncTracker is { } sync && _playback?.SubtitleSyncKey is { } key ? sync.ResetTimingAsync(key) : Task.CompletedTask;

    [RelayCommand]
    private void DismissSyncNotice() => _syncFeedback?.Dismiss();

    private void FollowSubtitleSync(IServerPlayback playback)
    {
        _syncFeedback ??= CreateSyncFeedback();
        _syncFeedback.Reset();
        _syncTracker = playback.SubtitleSync;
        if (_syncTracker is { } sync)
        {
            sync.Changed += OnSubtitleSyncChanged;
        }
        playback.SubtitleTimingChanged += OnSubtitleTimingChanged;
        playback.SubtitleCuesReloaded += OnSubtitleCuesReloaded;
    }

    private void ForgetSubtitleSync(IServerPlayback playback)
    {
        if (_syncTracker is { } sync)
        {
            sync.Changed -= OnSubtitleSyncChanged;
        }
        _syncTracker = null;
        playback.SubtitleTimingChanged -= OnSubtitleTimingChanged;
        playback.SubtitleCuesReloaded -= OnSubtitleCuesReloaded;
        _syncFeedback?.Reset();
        SubtitleTiming = SubtitleTimingPanel.Hidden;
    }

    private SubtitleSyncFeedback CreateSyncFeedback()
    {
        var feedback = new SubtitleSyncFeedback(_time, _dispatcher);
        feedback.NoticeChanged += (_, _) => SyncNotice = feedback.Notice;
        return feedback;
    }

    private void OnSubtitleSyncChanged(object? sender, EventArgs e)
    {
        if (sender == _syncTracker && _playback is { } playback)
        {
            TakeSubtitles(playback);
        }
    }

    private void OnSubtitleTimingChanged(object? sender, string key)
    {
        if (sender == _playback)
        {
            _syncFeedback?.TimingChanged(key);
        }
    }

    private void OnSubtitleCuesReloaded(object? sender, string key)
    {
        if (sender == _playback)
        {
            _syncFeedback?.CuesShown(key);
        }
    }

    // The rows' timing lines, the TIMING section and the indicator follow every change of the tracks and of their sync.
    private string? SyncStatusOf(Api.Playback.SubtitleInventoryItem item) =>
        _syncTracker is { IsAvailable: true } sync && sync.EntryOf(item.SyncKey) is { } entry ? SubtitleSyncText.Status(entry.State) : null;

    private void TakeSubtitleSync(IServerPlayback playback)
    {
        var sync = _syncTracker;
        var active = sync?.EntryOf(playback.SubtitleSyncKey);
        SubtitleTiming = sync is null ? SubtitleTimingPanel.Hidden : SubtitleTimingPanel.For(active, sync.IsAvailable, active is not null && sync.CanSync(active));
        _syncFeedback?.Update(Watched(playback, sync), playback.SubtitleSyncKey, active);
    }

    // The latest job this viewer started, among the file's subtitles, named by its track's language.
    private static WatchedSubtitleSync? Watched(IServerPlayback playback, SubtitleSyncTracker? sync)
    {
        var entry = sync?.Entries.Values
            .Where(e => e.WatchedJobId is { } id && e.Job?.Id == id)
            .OrderByDescending(e => e.Job!.CreatedAt, StringComparer.Ordinal)
            .FirstOrDefault();
        if (entry?.Job is not { } job)
        {
            return null;
        }
        var track = playback.SubtitleTracks.FirstOrDefault(t => t.SyncKey == entry.Key);
        string name = TrackText.LanguageName(track?.Language) is { Length: > 0 } language ? language
            : track?.Label is { Length: > 0 } label ? label
            : "Subtitles";
        return new WatchedSubtitleSync(entry.Key, name, job, entry.State.Timing);
    }
}
