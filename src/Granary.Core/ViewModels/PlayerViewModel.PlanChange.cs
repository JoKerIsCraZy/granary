using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels;

// While the stream is planned anew (a quality, file or track the stream cannot switch to, a seek it cannot reach), as the
// web player shows it: the settings button says "…" (QualityMenu), and the spinner shows once the wait has lasted, as the
// web's buffering spinner does. Granary's own: its mark turns in the corner for the whole change, named for it.
public sealed partial class PlayerViewModel
{
    /// <summary>How long a wait lasts before the spinner shows, so brief stalls and quick switches do not flash it (the web's 500 ms).</summary>
    public static readonly TimeSpan SpinnerDelay = TimeSpan.FromMilliseconds(500);

    private ITimer? _waitTimer;

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
    /// Waiting for data or for a new stream (buffering, reconnecting, a plan change) for <see cref="SpinnerDelay"/> or
    /// longer: the spinner shows over the picture. It goes as soon as the wait ends.
    /// </summary>
    [ObservableProperty]
    public partial bool IsWaiting { get; private set; }

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
        if (!IsBuffering && !IsChangingPlan)
        {
            StopWaitTimer();
            IsWaiting = false;
            return;
        }
        if (IsWaiting || _waitTimer is not null)
        {
            return;
        }
        ITimer? timer = null;
        timer = _time.CreateTimer(_ => _dispatcher.Post(() =>
        {
            // A timer stopped after it fired does not end the wait of the one after it.
            if (!ReferenceEquals(timer, _waitTimer))
            {
                return;
            }
            StopWaitTimer();
            IsWaiting = IsBuffering || IsChangingPlan;
        }), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _waitTimer = timer;
        timer.Change(SpinnerDelay, Timeout.InfiniteTimeSpan);
    }

    private void StopWaitTimer()
    {
        _waitTimer?.Dispose();
        _waitTimer = null;
    }
}
