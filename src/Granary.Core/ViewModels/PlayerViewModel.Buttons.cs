using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels;

// The right of the bar as Settings → Playback → Player buttons has it for the acting profile on this PC: which buttons may
// show, and in which order. Granary's own; Silo's clients have no such editor. A button that needs something, such as
// chapters, still shows only with it. The player reads the bar as it is made and follows it until it is disposed, so the
// minimized player does too.
public sealed partial class PlayerViewModel
{
    private readonly IPlayerButtonsLayout? _buttons;

    /// <summary>The bar's buttons in the profile's order, and which it hides; without a layout, as in tests that leave it out, all of them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSleepTimer))]
    public partial PlayerBar Bar { get; private set; } = PlayerBar.Default;

    private void FollowButtons()
    {
        if (_buttons is { } buttons)
        {
            Bar = buttons.Current;
            buttons.Changed += OnButtonsChanged;
        }
    }

    private void StopFollowingButtons()
    {
        if (_buttons is { } buttons)
        {
            buttons.Changed -= OnButtonsChanged;
        }
    }

    // Raised on the thread that changed the bar. On the UI thread it applies at once, so work queued there before it, such
    // as the title's end, finds a hidden Sleep timer off; from another thread it waits for its turn there. Each change
    // applies its own bar, so hidden and shown again before the UI thread gets to them still ends the timer.
    private void OnButtonsChanged(object? sender, PlayerBar bar)
    {
        if (_dispatcher.HasThreadAccess)
        {
            ApplyButtons(bar);
        }
        else
        {
            _dispatcher.Post(() => ApplyButtons(bar));
        }
    }

    private void ApplyButtons(PlayerBar bar)
    {
        // Posted before the player was left for good: nothing is left to change.
        if (_disposed || bar.Equals(Bar))
        {
            return;
        }
        bool hidesSleepTimer = Bar.SleepTimer && !bar.SleepTimer;
        Bar = bar;
        if (hidesSleepTimer)
        {
            EndSleepTimerWithItsButton();
        }
    }
}
