using Granary.Core.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Granary.App.Views;

// The keyboard map of the web player (docs/api-contract.md, 13.5) and clicks on the picture.
public sealed partial class PlayerPage
{
    // A second click within this time makes a double click, so the first click's play or pause waits for it.
    // Shorter than the system's double-click time: a click on the picture should answer at once.
    private static readonly TimeSpan DoubleClickWindow = TimeSpan.FromMilliseconds(250);

    private const double VolumeStep = 5;

    private DispatcherQueueTimer _clickTimer = null!;
    private bool _clickToggled;

    private void InitializeInput()
    {
        _clickTimer = DispatcherQueue.CreateTimer();
        _clickTimer.Interval = DoubleClickWindow;
        _clickTimer.IsRepeating = false;
        _clickTimer.Tick += (_, _) =>
        {
            _clickToggled = true;
            TogglePause();
        };
        PreviewKeyDown += OnPagePreviewKeyDown;
        KeyDown += OnPageKeyDown;
        PointerPressed += OnPagePointerPressed;
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

    private void TogglePause()
    {
        if (HasTransport)
        {
            ViewModel.TogglePauseCommand.Execute(null);
        }
    }

    // Any key shows the controls. Space is taken here, before a focused button would press itself with it:
    // on the player Space always plays or pauses, as on the web.
    private void OnPagePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        ShowControls();
        // On the Playing Next screen too, as long as the episode plays on in its corner; after its end Space presses
        // the focused button.
        if (e.Key == VirtualKey.Space && AcceptsShortcut() && !(ShowsNextUpScreen && ViewModel.Phase == PlayerPhase.Ended))
        {
            e.Handled = true;
            if (!e.KeyStatus.WasKeyDown)
            {
                TogglePause();
            }
        }
    }

    // The other keys arrive after the focused control had them: the volume slider keeps its arrow keys,
    // the seek bar its Left and Right.
    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!AcceptsShortcut())
        {
            return;
        }
        bool isRepeat = e.KeyStatus.WasKeyDown;
        if (ShowsNextUpScreen)
        {
            // The Playing Next screen's keys, as on the web: Esc closes, Enter plays the next episode.
            if (e.Key == VirtualKey.Escape && !isRepeat)
            {
                e.Handled = true;
                ViewModel.CloseCommand.Execute(null);
            }
            else if (e.Key == VirtualKey.Enter && !isRepeat && ViewModel.HasNextEpisode)
            {
                e.Handled = true;
                ViewModel.PlayNextNowCommand.Execute(null);
            }
            return;
        }
        switch (e.Key)
        {
            case VirtualKey.K when !isRepeat:
                TogglePause();
                break;
            case VirtualKey.F when !isRepeat:
                ToggleFullScreen();
                break;
            case VirtualKey.P when !isRepeat:
                SetMiniView(!IsMiniViewNow);
                break;
            case VirtualKey.M when !isRepeat:
                ViewModel.ToggleMuteCommand.Execute(null);
                break;
            case VirtualKey.C when !isRepeat:
                ViewModel.ToggleSubtitles();
                break;
            case VirtualKey.Escape when !isRepeat:
                // As on the web player, Esc first turns an intro pill away.
                if (ViewModel.DismissSkipPrompt())
                {
                    break;
                }
                if (IsFullScreenNow)
                {
                    SetFullScreen(false);
                }
                else if (IsMiniViewNow)
                {
                    SetMiniView(false);
                }
                else
                {
                    ViewModel.CloseCommand.Execute(null);
                }
                break;
            case VirtualKey.Left when ViewModel.CanSeek:
                ViewModel.SkipBackCommand.Execute(null);
                break;
            case VirtualKey.Right when ViewModel.CanSeek:
                ViewModel.SkipForwardCommand.Execute(null);
                break;
            case VirtualKey.Up:
                ViewModel.ChangeVolume(VolumeStep);
                break;
            case VirtualKey.Down:
                ViewModel.ChangeVolume(-VolumeStep);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // Modifier combinations belong to the app (Alt+Left, Ctrl+K); text fields, open menus and the subtitle panels keep
    // their keys.
    private bool AcceptsShortcut()
    {
        if (IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu) || IsDown(VirtualKey.Shift)
            || IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
        {
            return false;
        }
        return FocusManager.GetFocusedElement(XamlRoot) is not (TextBox or PasswordBox or RichEditBox) && !HasOpenMenu() && !IsPanelOpen;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // Flyouts and menus open in popups; tooltips do too, but they do not take the keyboard.
    private bool HasOpenMenu() =>
        XamlRoot is { } root && VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(popup => popup.Child is not ToolTip);

    private void OnSurfaceTapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.PointerDeviceType == PointerDeviceType.Touch)
        {
            // On touch a tap shows or hides the controls, as in the web player; the buttons play and pause.
            ToggleControls();
            return;
        }
        _clickToggled = false;
        _clickTimer.Stop();
        _clickTimer.Start();
    }

    private void OnSurfaceDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.PointerDeviceType == PointerDeviceType.Touch)
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
        ToggleFullScreen();
    }
}
