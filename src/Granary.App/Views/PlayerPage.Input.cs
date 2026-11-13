using Granary.App.Controls;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Player;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Granary.App.Views;

// The keyboard map of the web player (docs/api-contract.md, 13.5), the chapter keys of Silo's macOS app, and clicks on the
// picture. A key that belongs to a button Settings → Playback → Player buttons hides does nothing (Allows); F11 is the
// window's and always works, and so does a double click on the picture, which is no key of the button's.
public sealed partial class PlayerPage
{
    // A second click within this time makes a double click, so the first click's play or pause waits for it.
    // Shorter than the system's double-click time: a click on the picture should answer at once.
    private static readonly TimeSpan DoubleClickWindow = TimeSpan.FromMilliseconds(250);

    private const double VolumeStep = 5;

    private DispatcherQueueTimer _clickTimer = null!;
    private bool _clickToggled;

    // How far, in view pixels, the second click of a double click may be from the first.
    private const double DoubleClickReach = 16;

    // Tells a double click also when its first click went to the cursor's shield and only the second to WinUI.
    private readonly PlayerClicks _clicks = new(TimeProvider.System);

    // Whether the viewer last pressed a key rather than a mouse button: focus given back to a button shows then.
    private bool _lastInputWasKey;

    // Esc is leaving the player right now (CloseByKey).
    private bool _isLeavingByEsc;

    private void InitializeInput()
    {
        _clickTimer = DispatcherQueue.CreateTimer();
        _clickTimer.Interval = DoubleClickWindow;
        _clickTimer.IsRepeating = false;
        WeakTick.Subscribe(_clickTimer, this, static page =>
        {
            page._clickToggled = true;
            page.TogglePause();
        });
        PreviewKeyDown += OnPagePreviewKeyDown;
        KeyDown += OnPageKeyDown;
        PointerPressed += OnPagePointerPressed;
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) => PressedOnPage(e.Pointer.PointerDeviceType)), handledEventsToo: true);
    }

    // Any press on the page, seen by WinUI or by the cursor's shield (PlayerPage.Shield.cs).
    private void PressedOnPage(PointerDeviceType device)
    {
        _lastInputWasKey = _movesThroughControls = false;
        // Only Esc, Esc is the way out that keeps full screen for the next title (PlayerFullScreen).
        _fullScreen.ForgetEscape();
        ShowCursorForPress(device);
    }

    // A press on the picture, or anything else that does not take focus, would bubble up to the window's root
    // scroll viewer, which then takes the focus, and the page's keys with it. The page takes it instead. Other
    // buttons than the left one pass on: the mouse back button belongs to the window.
    private void OnPagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.Handled && e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
        {
            e.Handled = true;
            Focus(FocusState.Pointer);
        }
    }

    private void StopInput() => _clickTimer.Stop();

    // The time Windows allows between the two clicks of a double click, as the viewer set it.
    private static TimeSpan SystemDoubleClickTime() => TimeSpan.FromMilliseconds(GetDoubleClickTime());

    private void TogglePause()
    {
        if (HasTransport)
        {
            ViewModel.TogglePauseCommand.Execute(null);
        }
    }

    // Any key shows the controls, not the cursor: only the mouse brings that back. Space is taken here, before a focused
    // button would press itself with it:
    // on the player Space always plays or pauses, as on the web. In the bar a focused button keeps it. OK (Enter) with
    // focus on the picture shows the controls with focus on Play/Pause, as Silo's TV app reveals them
    // (requestIdleOverlayFocus); there the controls never show without focus, here a pointer shows them too, and an air
    // mouse keeps them up, so OK does it whether they show already or not. A button that shows while they are hidden
    // (the sync notice's ✕, the skip pill) keeps OK. A held OK acts once: its repeats neither press the button it
    // landed on nor bring the controls back after a skip. The chapter keys are taken here too (JumpsChapter).
    private void OnPagePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        _lastInputWasKey = true;
        if (e.Key != VirtualKey.Escape)
        {
            // Only Esc, Esc is the way out that keeps full screen for the next title (PlayerFullScreen).
            _fullScreen.ForgetEscape();
        }
        if (e.Key == VirtualKey.Enter && e.KeyStatus.WasKeyDown)
        {
            e.Handled = true;
            return;
        }
        bool okOnHidden = e.Key == VirtualKey.Enter && !_areControlsVisible && !HasShownButtonFocus();
        if (okOnHidden && SkipsFromKey())
        {
            e.Handled = true;
            return;
        }
        _controlsShownBeforeKey = _areControlsVisible;
        ShowControls();
        if (JumpsChapter(e.Key, e.KeyStatus.WasKeyDown))
        {
            e.Handled = true;
            return;
        }
        bool okOnPicture = e.Key == VirtualKey.Enter && !HasFocusInControls() && !HasShownButtonFocus();
        if (okOnPicture && !IsDocked && !ShowsNextUpScreen && AcceptsShortcut())
        {
            e.Handled = true;
            FocusTransport();
            return;
        }
        // On the Playing Next screen too, as long as the episode plays on in its corner; after its end Space presses
        // the focused button.
        if (e.Key == VirtualKey.Space && AcceptsShortcut() && !(ShowsNextUpScreen && ViewModel.Phase == PlayerPhase.Ended)
            && !(IsDocked && FocusManager.GetFocusedElement(XamlRoot) is ButtonBase))
        {
            e.Handled = true;
            if (!e.KeyStatus.WasKeyDown)
            {
                TogglePause();
            }
        }
    }

    // The other keys arrive after the focused control had them: the volume slider keeps its arrow keys,
    // the seek bar its Left and Right. On the picture Left and Right skip, as on the web player and Silo's TV app; Up and
    // Down do as on the TV app (a remote has its own volume buttons), or set the volume as on the web, as this PC's
    // settings say (IPlaybackDevicePreferences.UpDown). In the minimized bar they always set the volume.
    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!AcceptsShortcut())
        {
            if (e.Key == VirtualKey.Escape)
            {
                // An Esc a menu or panel takes is not on the way out of the player.
                _fullScreen.ForgetEscape();
            }
            return;
        }
        bool isRepeat = e.KeyStatus.WasKeyDown;
        // The bar takes the transport's keys only: F, the mini view, subtitles and Esc are the full player's. F11 is the
        // window's everywhere (MainWindow.FullScreen.cs).
        if (IsDocked && (e.Key is VirtualKey.F or VirtualKey.P or VirtualKey.C || IsBack(e.Key)))
        {
            return;
        }
        if (ShowsNextUpScreen)
        {
            // The Playing Next screen's keys, as on the web: Esc closes, Enter plays the next episode.
            if (IsBack(e.Key) && !isRepeat)
            {
                e.Handled = true;
                CloseByKey(e.Key);
            }
            else if (e.Key == VirtualKey.Enter && !isRepeat && ViewModel.HasNextEpisode)
            {
                e.Handled = true;
                ViewModel.PlayNextNowCommand.Execute(null);
            }
            return;
        }
        if (MovesThroughControls(e.Key) || HidesControls(e.Key, isRepeat))
        {
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case VirtualKey.K when !isRepeat:
                TogglePause();
                break;
            case VirtualKey.F when !isRepeat:
                if (Allows(PlayerKey.FullScreen))
                {
                    ToggleFullScreen();
                }
                break;
            case VirtualKey.P when !isRepeat:
                if (Allows(PlayerKey.MiniView))
                {
                    SetMiniView(!_modes.IsMiniView);
                }
                break;
            case VirtualKey.M when !isRepeat:
                if (Allows(PlayerKey.Mute))
                {
                    ViewModel.ToggleMuteCommand.Execute(null);
                }
                break;
            case VirtualKey.C when !isRepeat:
                ViewModel.ToggleSubtitles();
                break;
            case VirtualKey.Escape or VirtualKey.GoBack or VirtualKey.Back when !isRepeat:
                // As on the web player, Esc first turns an intro pill away; the marker panel closes before the player. A
                // remote's Back (Browser Back) and Backspace do as Esc does.
                if (ViewModel.DismissSkipPrompt())
                {
                    // An Esc the pill took is not on the way out of the player.
                    _fullScreen.ForgetEscape();
                    break;
                }
                if (ViewModel.MarkerEditor.IsEditing)
                {
                    ViewModel.MarkerEditor.Cancel();
                    _fullScreen.ForgetEscape();
                    break;
                }
                // Full screen is the whole window's: Esc leaves it for the app too. A remote's Back closes the player and
                // keeps full screen, as a media PC on a TV runs in it. Esc on its way out of the player: the next title
                // still opens in full screen when the next Esc leaves the player soon after (PlayerFullScreen). Marked
                // after the change: WindowModeService raises Changed within SetFullScreen, and OnWindowModeChanged
                // forgets an Esc, so marking first would be undone at once.
                if (_modes.IsFullScreen && e.Key == VirtualKey.Escape)
                {
                    _modes.SetFullScreen(false);
                    _fullScreen.EscapedFullScreen();
                }
                else if (_modes.IsMiniView)
                {
                    SetMiniView(false);
                }
                else
                {
                    CloseByKey(e.Key);
                }
                break;
            case VirtualKey.Left when ViewModel.CanSeek:
                ViewModel.SkipBackCommand.Execute(null);
                break;
            case VirtualKey.Right when ViewModel.CanSeek:
                ViewModel.SkipForwardCommand.Execute(null);
                break;
            // While the title loads or after it failed there are no controls to show or menus to open: the volume, as before.
            case VirtualKey.Up when IsDocked || !UpDownMovesFocus || !HasTransport:
                ChangeVolume(VolumeStep);
                break;
            case VirtualKey.Down when IsDocked || !UpDownMovesFocus || !HasTransport:
                ChangeVolume(-VolumeStep);
                break;
            case VirtualKey.Up:
                if (!isRepeat)
                {
                    FocusTransport();
                }
                break;
            case VirtualKey.Down:
                if (!isRepeat)
                {
                    OpenPlaybackMenu();
                }
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // OK while a skip pill shows and the controls are hidden does what the pill says (Skip Intro, Recap, Credits or
    // Preview, or Watch Intro after it skipped itself), and the controls stay away, as on Silo's TV app. With the controls up
    // the focused button keeps OK: taking it there made every OK skip for as long as the intro ran.
    private bool SkipsFromKey()
    {
        if (IsCompact || ShowsNextUpScreen || !ViewModel.HasSkipPrompt || !AcceptsShortcut() || !ViewModel.SkipCommand.CanExecute(null))
        {
            return false;
        }
        ViewModel.SkipCommand.Execute(null);
        return true;
    }

    // Ctrl+Right and Ctrl+Left jump between chapters, as ⌘→ and ⌘← on Silo's macOS app, through the Chapters menu's seek. They
    // are taken on the way down, before a focused volume slider would set the volume with them, and never skip or move between
    // the controls. A held key jumps once: each repeat would be another seek, and in a Watch Party another request to the room.
    // Without chapters, while the title cannot be sought and on the Playing Next screen they pass on.
    private bool JumpsChapter(VirtualKey key, bool isRepeat)
    {
        if (!IsChapterKey(key) || ShowsNextUpScreen || !ViewModel.CanSeek || !ViewModel.HasChapters || !Allows(PlayerKey.Chapters)
            || !AcceptsShortcut(key))
        {
            return false;
        }
        if (!isRepeat)
        {
            ViewModel.SeekToAdjacentChapter(forward: key == VirtualKey.Right);
        }
        return true;
    }

    // Whether a key of a bar button does what it does: while Settings shows the button; the minimized bar keeps its own
    // Mute and the volume keys.
    private bool Allows(PlayerKey key) => ViewModel.Bar.AllowsKey(key, IsDocked);

    private void ChangeVolume(double delta)
    {
        if (Allows(PlayerKey.Volume))
        {
            ViewModel.ChangeVolume(delta);
        }
    }

    // A button with focus that is not one of the controls, so shows while they are hidden.
    private bool HasShownButtonFocus() => FocusManager.GetFocusedElement(XamlRoot) is ButtonBase button && !IsInControls(button);

    // Up and Down do as on Silo's TV app, unless this PC's settings give them the volume.
    private bool UpDownMovesFocus => _devicePreferences.UpDown == PlayerUpDown.ControlsAndMenus;

    // Down from the picture opens the menu a viewer most likely wants, as Silo's TV app opens its panel on audio, else
    // subtitles, else video (preferredPlaybackHudTab): here the audio menu when there is a track to choose, else the
    // subtitles', else the playback settings, of those Settings shows (PlayerBar.MenuOnDown); with none, nothing. Back
    // closes it onto its button, among the shown controls.
    private void OpenPlaybackMenu()
    {
        if (IsMiniView)
        {
            FocusTransport();
            return;
        }
        if (ViewModel.Bar.MenuOnDown(ViewModel.CanChooseAudio, ViewModel.HasSubtitleMenu) is not { } shown)
        {
            return;
        }
        var (button, menu) = shown switch
        {
            PlayerButton.Audio => (AudioButton, AudioMenu),
            PlayerButton.Subtitles => (SubtitleButton, SubtitleMenu),
            _ => (SettingsButton, SettingsMenu),
        };
        _movesThroughControls = true;
        RestartHideTimer();
        button.Focus(FocusState.Keyboard);
        menu.ShowAt(button);
    }

    // A remote's OK showed the controls (on the picture, or on the bar's picture): focus on Play/Pause, and the arrows
    // move between the controls until they hide.
    private void FocusTransport()
    {
        _movesThroughControls = true;
        RestartHideTimer();
        // The mini view shows only its own Play/Pause.
        (IsMiniView ? MiniPlayPauseButton : PlayPauseButton).Focus(FocusState.Keyboard);
    }

    // After a remote's OK showed the controls, with focus still on one of them, the arrows move between them, as on Silo's
    // TV app; the seek bar keeps Left and Right, which it takes first. Otherwise, a tabbed-to button too, Left and Right
    // skip, as on the web player. Up and Down on a shown control move and turn the arrows over to the controls, however
    // focus got there (an air mouse's click on the seek bar); with Up and Down set to the volume only on the seek bar,
    // which has no use for them, and elsewhere they keep the web's volume.
    private bool MovesThroughControls(VirtualKey key)
    {
        var direction = key switch
        {
            VirtualKey.Left => FocusNavigationDirection.Left,
            VirtualKey.Right => FocusNavigationDirection.Right,
            VirtualKey.Up => FocusNavigationDirection.Up,
            VirtualKey.Down => FocusNavigationDirection.Down,
            _ => FocusNavigationDirection.None,
        };
        if (direction == FocusNavigationDirection.None || !_controlsShownBeforeKey || !HasFocusInControls())
        {
            return false;
        }
        bool upOrDown = direction is FocusNavigationDirection.Up or FocusNavigationDirection.Down
            && (UpDownMovesFocus || FocusManager.GetFocusedElement(XamlRoot) is SeekBar);
        if (!_movesThroughControls && !upOrDown)
        {
            return false;
        }
        _movesThroughControls = true;
        RestartHideTimer();
        MoveFocusPastVolumeSlider(direction);
        return true;
    }

    // The arrows pass the volume slider by: on it every arrow would change the volume, and no remote could get past it to
    // the buttons beside it. Tab still reaches it; a remote sets the volume with its own volume keys, with M, or with Up
    // and Down where the settings give them the volume. For the move the slider is no stop at all. Its place is not
    // handed over as FindNextElementOptions.ExclusionRect: WinUI multiplies that rectangle by the display's scale and
    // holds it against bounds it has divided by the scale, so at any scale but 100 % (a TV's 150 %) it lay somewhere else
    // and the slider took the focus (FocusRectangleTests).
    private void MoveFocusPastVolumeSlider(FocusNavigationDirection direction)
    {
        bool wasTabStop = VolumeSlider.IsTabStop;
        VolumeSlider.IsTabStop = false;
        try
        {
            FocusManager.TryMoveFocus(direction, new FindNextElementOptions { SearchRoot = this });
        }
        finally
        {
            VolumeSlider.IsTabStop = wasTabStop;
        }
    }

    // The remote's Back (Browser Back, Backspace) with the controls shown hides them, as the TV app's ladder has it; Esc
    // does as on the web player. Not while paused, where hidden controls would only leave a still picture, so Back closes;
    // a skip pill and the marker editor take Back first (the switch below). A stream reloading for a subtitle just picked
    // counts as playing. Focus leaves the hidden buttons for the page: kept, it would hold the controls up from then on. The
    // cursor goes with the controls, as when they hide by themselves (SetControlsVisible).
    private bool HidesControls(VirtualKey key, bool isRepeat)
    {
        if (key is not (VirtualKey.GoBack or VirtualKey.Back) || isRepeat || !_controlsShownBeforeKey || IsCompact
            || ViewModel.IsPaused || ViewModel.HasSkipPrompt || ViewModel.MarkerEditor.IsEditing)
        {
            return false;
        }
        _hideTimer.Stop();
        Focus(FocusState.Programmatic);
        SetControlsVisible(false);
        return true;
    }

    // Esc, a remote's Back (Browser Back) and Backspace. Typing never gets here (AcceptsShortcut).
    private static bool IsBack(VirtualKey key) => key is VirtualKey.Escape or VirtualKey.GoBack or VirtualKey.Back;

    // Leaves the player with a back key. Esc is the one leave an Esc out of full screen just before counts for
    // (PlayerFullScreen): the close runs within this call, down to Leave, which reads _isLeavingByEsc.
    private void CloseByKey(VirtualKey key)
    {
        _isLeavingByEsc = key == VirtualKey.Escape;
        try
        {
            ViewModel.CloseCommand.Execute(null);
        }
        finally
        {
            _isLeavingByEsc = false;
        }
    }

    // Modifier combinations belong to the app (Alt+Left, Ctrl+K), all but the chapter keys (JumpsChapter, the only caller
    // that passes a key); text fields, open menus and the subtitle panels keep their keys.
    private bool AcceptsShortcut(VirtualKey key = VirtualKey.None)
    {
        if (HasModifier() && !IsChapterKey(key))
        {
            return false;
        }
        return FocusManager.GetFocusedElement(XamlRoot) is not (TextBox or PasswordBox or RichEditBox) && !HasOpenMenu() && !IsPanelOpen;
    }

    // Ctrl+Left and Ctrl+Right with no other modifier: Ctrl takes the place of the Mac's ⌘.
    private static bool IsChapterKey(VirtualKey key) =>
        (key is VirtualKey.Left or VirtualKey.Right) && IsDown(VirtualKey.Control)
        && !IsDown(VirtualKey.Menu) && !IsDown(VirtualKey.Shift) && !IsDown(VirtualKey.LeftWindows) && !IsDown(VirtualKey.RightWindows);

    private static bool HasModifier() =>
        IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu) || IsDown(VirtualKey.Shift)
        || IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows);

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // Flyouts and menus open in popups; tooltips do too, but they do not take the keyboard.
    private bool HasOpenMenu() =>
        XamlRoot is { } root && VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(popup => popup.Child is not ToolTip);

    private void OnSurfaceTapped(object sender, TappedRoutedEventArgs e) => SurfaceTapped(e.PointerDeviceType);

    private void OnSurfaceDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => SurfaceDoubleTapped(e.PointerDeviceType);

    // A click or a tap on the picture, from WinUI or from the cursor's shield while that lies over it.
    private void SurfaceTapped(PointerDeviceType device)
    {
        if (device == PointerDeviceType.Touch)
        {
            // On touch a tap shows or hides the controls, as in the web player; the buttons play and pause.
            ToggleControls();
            return;
        }
        // The second click of a double click that WinUI reports as a click of its own: it never saw the first, which went
        // to the cursor's shield and took the shield away.
        if (ScreenPointer() is { } at
            && _clicks.IsSecond(at.X, at.Y, SystemDoubleClickTime(), DoubleClickReach * (XamlRoot?.RasterizationScale ?? 1)))
        {
            SurfaceDoubleTapped(device);
            return;
        }
        _clickToggled = false;
        _clickTimer.Stop();
        _clickTimer.Start();
    }

    private void SurfaceDoubleTapped(PointerDeviceType device)
    {
        // Not a second time for one pair: WinUI may tell a double click the page already told from its two clicks.
        if (device == PointerDeviceType.Touch || !_clicks.TakesDouble(SystemDoubleClickTime()))
        {
            return;
        }
        if (_clickTimer.IsRunning)
        {
            _clickTimer.Stop();
        }
        else if (_clickToggled)
        {
            // A slow double click: the first click already played or paused; undo that.
            TogglePause();
        }
        _clickToggled = false;
        // Also with the Full screen button hidden in Settings: hiding it takes its key F, not the way into full screen.
        ToggleFullScreen();
    }
}
