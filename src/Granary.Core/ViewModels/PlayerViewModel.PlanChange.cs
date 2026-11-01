using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels;

// While the stream is planned anew (a quality, file or track the stream cannot switch to, a seek it cannot reach), as the
// web player shows it: the settings button says "…" (QualityMenu), and the spinner shows only while the picture stands
// still, as the web's comes from the video's waiting event. Granary's own: its mark turns in the corner for the whole
// change, named for it.
public sealed partial class PlayerViewModel
{
    /// <summary>How long a wait lasts before the spinner shows, so brief stalls and quick switches do not flash it (the web's 500 ms).</summary>
    public static readonly TimeSpan SpinnerDelay = TimeSpan.FromMilliseconds(500);

    private ITimer? _waitTimer;
    private bool _disposed;

    /// <summary>The plan change in flight; <see cref="PlanChange.None"/> while none is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChangingPlan), nameof(PlanChangeText))]
    public partial PlanChange PlanChange { get; private set; }

    public bool IsChangingPlan => PlanChange != PlanChange.None;

    /// <summary>What the change does, for screen readers: "Switching quality"; empty while none is in flight.</summary>
    public string PlanChangeText => PlanChange switch
    {
        PlanChange.Quality => "Switching quality",
        PlanChange.Version => "Switching version",
        PlanChange.Audio => "Switching audio",
        PlanChange.Subtitles => "Switching subtitles",
        PlanChange.Seek => "Seeking",
        PlanChange.Restart => "Restarting playback",
        _ => "",
    };

    /// <summary>
    /// The picture has stood still for <see cref="SpinnerDelay"/> or longer while it waits: for data (buffering,
    /// reconnecting) or, during a plan change, for the new stream. The spinner shows over the picture; it goes as soon as
    /// the picture moves. While the old stream still plays during a change, it stays away.
    /// </summary>
    [ObservableProperty]
    public partial bool IsWaiting { get; private set; }

    // The picture stands still for a reason the viewer did not choose: data that does not come, or the new stream of a
    // plan change once the old one no longer plays.
    private bool WaitsForPicture => IsBuffering || (IsChangingPlan && !(_engineState.IsLoaded && _engineState.HasStarted));

    private void OnPlaybackPlanChanged(object? sender, EventArgs e)
    {
        if (sender == _playback && _playback is { } playback)
        {
            PlanChange = playback.PlanChange;
        }
    }

    partial void OnPlanChangeChanged(PlanChange value)
    {
        ShowSettings();
        FollowWait();
    }

    partial void OnIsBufferingChanged(bool value) => FollowWait();

    // A wait that began is shown once it has lasted; one that ends takes the spinner away at once.
    private void FollowWait()
    {
        if (!WaitsForPicture)
        {
            StopTimer(ref _waitTimer);
            IsWaiting = false;
            return;
        }
        if (IsWaiting || _waitTimer is not null)
        {
            return;
        }
        _waitTimer = After(SpinnerDelay, timer =>
        {
            if (ReferenceEquals(timer, _waitTimer))
            {
                StopTimer(ref _waitTimer);
                IsWaiting = WaitsForPicture;
            }
        });
    }

    // A one-shot timer that runs on the UI thread. The callback gets its timer, so one stopped after it fired can tell.
    private ITimer? After(TimeSpan delay, Action<ITimer> fire)
    {
        if (_disposed)
        {
            return null;
        }
        ITimer? timer = null;
        timer = _time.CreateTimer(_ => _dispatcher.Post(() => fire(timer!)), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        timer.Change(delay, Timeout.InfiniteTimeSpan);
        return timer;
    }

    private static void StopTimer(ref ITimer? timer)
    {
        timer?.Dispose();
        timer = null;
    }

    // For good: work the engine posted before the dispose starts no timer after it.
    private void StopPlanChangeTimers()
    {
        _disposed = true;
        StopTimer(ref _waitTimer);
    }
}
