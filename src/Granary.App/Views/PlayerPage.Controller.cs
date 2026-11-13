using Granary.App.Controls;
using Granary.Core.Controller;
using Granary.Core.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Granary.App.Views;

// A game controller in the full player (docs/ui-design.md, Game controller). The window reads the pad and hands over what
// PadInterpreter made of it (MainWindow.Controller.cs); an open menu takes its D-pad, A and B there. Focus on the picture
// is the pad's resting place: A plays or pauses, Left and Right seek, Up and Down bring the bar. With focus on a control
// the D-pad moves and A presses, until the bar hides and focus is back on the picture, as for a remote.
public sealed partial class PlayerPage
{
    // A bumper's jump where the file has no chapters.
    private const double BigStepSeconds = 300;

    // Where the seek being made began: its steps count from here, however long it takes.
    private double? _padSeekOrigin;

    // The pad's own seek is on its way through the seek bar: it replaces nothing (OnViewerSought).
    private bool _isSendingPadSeek;

    /// <summary>
    /// Another seek of the viewer's (a key, the mouse, a skip, a chapter, the seek bar's own step) replaced one a pad was
    /// making: the window drops it in the interpreter too.
    /// </summary>
    internal event EventHandler? PadSeekReplaced;

    /// <summary>What a pad's reading means here right now.</summary>
    internal PadContext PadContext => new(
        PadMode.Player,
        HasOpenMenu() || IsPanelOpen ? PadFocus.Menu
            : ShowsNextUpScreen || (HasShownButtonFocus() && FocusedPrompt() is null) || (_areControlsVisible && HasFocusInControls())
                ? PadFocus.Controls
            : PadFocus.Picture,
        HasPrompt: HasPadPrompt,
        CanSeek: HasTransport && ViewModel.CanSeek,
        ViewModel.SkipBackSeconds,
        ViewModel.SkipForwardSeconds,
        PositionSeconds: _padSeekOrigin ?? SeekBar.DisplayedSeconds,
        ViewModel.DurationSeconds);

    // Skip Intro (or Recap, Credits, Preview) and the next episode's card: A takes them rather than pausing. They take focus
    // as they appear (FocusPrompt), and focus on one of them counts as on the picture, as for the arrow keys.
    private bool HasPadPrompt =>
        !IsCompact && !ShowsNextUpScreen
        && ((ViewModel.HasSkipPrompt && ViewModel.SkipCommand.CanExecute(null)) || ViewModel.IsNextUpCardShown);

    internal void TakePad(PadAction action)
    {
        _lastInputWasKey = true;
        // Only Esc, Esc is the way out that keeps full screen for the next title (PlayerFullScreen).
        _fullScreen.ForgetEscape();
        switch (action.Kind)
        {
            case PadActionKind.PlayPause or PadActionKind.TogglePause:
                TogglePause();
                ShowControls();
                break;
            case PadActionKind.TriggerPrompt:
                // The prompt's button that has focus, as Enter presses it; one that did not get focus, all the same.
                if (FocusedPrompt() is { } prompt)
                {
                    PadControls.Press(prompt);
                }
                else if (!SkipsFromKey() && ViewModel.IsNextUpCardShown)
                {
                    ViewModel.PlayNextNowCommand.Execute(null);
                }
                break;
            case PadActionKind.Activate:
                PressPadFocus();
                break;
            case PadActionKind.MoveFocus:
                MovePadFocus(PadControls.WayOf(action.Direction));
                break;
            case PadActionKind.EnterBar when HasTransport && !ShowsNextUpScreen:
                ShowControls();
                FocusTransport();
                break;
            case PadActionKind.Back:
                GoBackByPad();
                break;
            case PadActionKind.SeekPreview:
                ShowPadSeek(action.Amount);
                break;
            case PadActionKind.SeekCommit:
                SendPadSeek(action.Amount);
                break;
            case PadActionKind.SeekCancel:
                CancelPadSeek();
                break;
            case PadActionKind.PreviousChapter:
                JumpByPad(forward: false);
                break;
            case PadActionKind.NextChapter:
                JumpByPad(forward: true);
                break;
            case PadActionKind.Volume:
                ChangeVolume(action.Amount);
                ShowControls();
                break;
            case PadActionKind.AudioAndSubtitles:
                OpenByPad(OpenPlaybackMenu);
                break;
            case PadActionKind.PlayerSettings when ViewModel.Bar.Settings && !IsMiniView:
                OpenByPad(() =>
                {
                    _movesThroughControls = true;
                    RestartHideTimer();
                    SettingsButton.Focus(FocusState.Keyboard);
                    SettingsMenu.ShowAt(SettingsButton);
                });
                break;
            case PadActionKind.PlaybackInfo:
                ViewModel.TogglePlaybackInfoCommand.Execute(null);
                break;
            case PadActionKind.ToggleSubtitles:
                ViewModel.ToggleSubtitles();
                break;
        }
    }

    /// <summary>The seek a pad was making is dropped and nothing seeks: the pad went away, or the window lost the foreground.</summary>
    internal void CancelPadSeek()
    {
        _padSeekOrigin = null;
        SeekBar.ShowTarget(null);
    }

    // Any seek of the viewer's but the pad's own: a seek the pad was making gives way to it. A key's target on the seek bar
    // was taken from what the bar showed, the pad's preview among it, before this. As the bar the player makes no pad
    // seek: the pad is on the pages then.
    private void OnViewerSought(object? sender, EventArgs e)
    {
        if (_isSendingPadSeek || IsDocked)
        {
            return;
        }
        CancelPadSeek();
        PadSeekReplaced?.Invoke(this, EventArgs.Empty);
    }

    // Y and Menu open their menu, and close whichever menu is open.
    private void OpenByPad(Action open)
    {
        if (HasOpenMenu())
        {
            CloseMenus();
        }
        else if (HasTransport && !ShowsNextUpScreen && !IsPanelOpen)
        {
            ShowControls();
            open();
        }
    }

    private void PressPadFocus()
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused)
        {
            return;
        }
        KeepControlsForPad();
        PadControls.Press(focused);
    }

    // The D-pad between the controls, as a remote's arrows (MovesThroughControls): the seek bar seeks with Left and Right,
    // and the volume slider is passed by, since the right stick sets the volume. In a panel it stays in the panel.
    private void MovePadFocus(FocusNavigationDirection way)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused)
        {
            Focus(FocusState.Programmatic);
            return;
        }
        KeepControlsForPad();
        if (PadControls.Nudge(focused, way))
        {
            return;
        }
        DependencyObject scope = AddSubtitlesPanel.Visibility == Visibility.Visible ? AddSubtitlesPanel
            : ViewModel.IsAppearanceOpen ? AppearancePanel
            : this;
        bool wasTabStop = VolumeSlider.IsTabStop;
        VolumeSlider.IsTabStop = false;
        try
        {
            PadControls.Move(way, scope);
        }
        finally
        {
            VolumeSlider.IsTabStop = wasTabStop;
        }
    }

    // While the pad is on the controls they wait as long as for a remote, and focus goes back to the picture when they hide.
    private void KeepControlsForPad()
    {
        if (HasFocusInControls())
        {
            _movesThroughControls = true;
            RestartHideTimer();
        }
    }

    // B, as a remote's Back: a panel closes, the Playing Next screen closes the player, the shown controls hide, then an
    // intro pill is turned away, and at last the player closes. The window has closed an open menu before this.
    private void GoBackByPad()
    {
        if (AddSubtitlesPanel.Visibility == Visibility.Visible)
        {
            CloseAddSubtitles();
        }
        else if (ViewModel.IsAppearanceOpen)
        {
            ViewModel.CloseAppearanceCommand.Execute(null);
        }
        else if (ShowsNextUpScreen)
        {
            CloseByKey(VirtualKey.GoBack);
        }
        else
        {
            _controlsShownBeforeKey = _areControlsVisible;
            if (!HidesControls(VirtualKey.GoBack, isRepeat: false))
            {
                StepBack(VirtualKey.GoBack);
            }
        }
    }

    // A seek being made shows on the seek bar, as a drag does, from where the first step found the title.
    private void ShowPadSeek(double offset)
    {
        if (!SeekBar.CanSeek)
        {
            return;
        }
        _padSeekOrigin ??= SeekBar.DisplayedSeconds;
        KeepControlsUp();
        SeekBar.ShowTarget(_padSeekOrigin.Value + offset);
    }

    // The one seek, through the seek bar as its own keys seek.
    private void SendPadSeek(double offset)
    {
        double origin = _padSeekOrigin ?? SeekBar.DisplayedSeconds;
        CancelPadSeek();
        if (SeekBar.CanSeek)
        {
            KeepControlsUp();
            _isSendingPadSeek = true;
            try
            {
                SeekBar.RequestSeek(origin + offset);
            }
            finally
            {
                _isSendingPadSeek = false;
            }
        }
    }

    // A bumper: the next chapter, or this one's start and the one before, as Ctrl+Right and Ctrl+Left; without chapters
    // (or with the Chapters button hidden in Settings) a big step.
    private void JumpByPad(bool forward)
    {
        if (!HasTransport || ShowsNextUpScreen || !SeekBar.CanSeek)
        {
            return;
        }
        KeepControlsUp();
        if (ViewModel.HasChapters && Allows(PlayerKey.Chapters))
        {
            ViewModel.SeekToAdjacentChapter(forward);
        }
        else
        {
            SeekBar.RequestSeek(SeekBar.DisplayedSeconds + (forward ? BigStepSeconds : -BigStepSeconds));
        }
    }

    // Shown, and the wait for hiding begun again. A scrub asks sixty times a second: only what changes is done.
    private void KeepControlsUp()
    {
        if (_areControlsVisible)
        {
            RestartHideTimer();
        }
        else
        {
            ShowControls();
        }
    }
}
