using System.Numerics;
using Granary.App.Controls;
using Granary.Core.ViewModels.Player;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Granary.App.Views;

// The controls fade out after a few seconds of playback without pointer movement or a key, wherever the pointer rests in
// the full player, and stay while there is something open or under way in them, or the pointer is over the mini view
// (PlayerControlsIdle). The cursor goes with them over the picture and comes back only with the mouse (PlayerCursor).
public sealed partial class PlayerPage
{
    private static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(3);

    // With a remote's focus in the controls they wait as long as Silo's TV app's (CONTROLS_AUTO_HIDE_MS).
    private static readonly TimeSpan RemoteHideDelay = TimeSpan.FromSeconds(5);

    // How often a cursor the hidden controls left showing off the picture is looked at again.
    private static readonly TimeSpan CursorLookInterval = TimeSpan.FromSeconds(1);

    private const float ChromeSlide = 8;

    // Between the skip pill and the seek bar's row, while the controls show.
    private const double SkipPromptGap = 12;

    private DispatcherQueueTimer _hideTimer = null!;
    private DispatcherQueueTimer _cursorTimer = null!;
    private bool _areControlsVisible = true;

    // Whether the cursor shows, and whether the page's cursor is the blank one now.
    private readonly PlayerCursor _cursor = new(TimeProvider.System);
    private bool _isCursorHidden;

    // What tells the page to look at the cursor again, from its load until it is left.
    private MainWindow? _cursorWindow;
    private XamlRoot? _cursorRoot;

    // The hold last written to the log: a pause would otherwise write one every 3 s.
    private ControlsHold _loggedHold;
    private readonly ILogger _autoHideLog = App.Services.GetRequiredService<ILogger<PlayerPage>>();

    // Whether the controls were shown before the key now being handled showed them, and whether a remote's OK showed
    // them, so the arrows move between them until they hide (PlayerPage.Input.cs).
    private bool _controlsShownBeforeKey;
    private bool _movesThroughControls;

    private void InitializeAutoHide()
    {
        // The bar changes height with the window, and the lift with it.
        BottomBar.SizeChanged += (_, _) =>
        {
            LiftSubtitles();
            PlaceSkipPrompt();
        };
        SizeChanged += (_, _) =>
        {
            LiftSubtitles();
            SizeNextUpStill();
        };
        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = HideDelay;
        _hideTimer.IsRepeating = false;
        WeakTick.Subscribe(_hideTimer, this, static page => page.HideControlsWhenIdle());
        _cursorTimer = DispatcherQueue.CreateTimer();
        _cursorTimer.Interval = CursorLookInterval;
        _cursorTimer.IsRepeating = true;
        WeakTick.Subscribe(_cursorTimer, this, static page => page.LookAtCursorAgain("a while later", reassert: false));
        PointerMoved += OnPagePointerMoved;
    }

    // As the page loads: the pointer resting where it is does not count as a move, and the window's changes have the
    // cursor looked at again.
    private void StartAutoHide()
    {
        if (GetCursorPos(out var at))
        {
            _cursor.RestsAt(at.X, at.Y);
        }
        _cursorWindow = App.Services.GetRequiredService<MainWindow>();
        _cursorWindow.Activated += OnWindowActivatedForCursor;
        _cursorRoot = XamlRoot;
        _cursorRoot.Changed += OnXamlRootChangedForCursor;
        _modes.Changed += OnWindowModeChangedForCursor;
    }

    private void StopAutoHide()
    {
        _hideTimer.Stop();
        _cursorTimer.Stop();
        if (_cursorWindow is not null)
        {
            _cursorWindow.Activated -= OnWindowActivatedForCursor;
            _cursorWindow = null;
        }
        if (_cursorRoot is not null)
        {
            _cursorRoot.Changed -= OnXamlRootChangedForCursor;
            _cursorRoot = null;
        }
        _modes.Changed -= OnWindowModeChangedForCursor;
        _cursor.Show();
        ApplyCursor();
    }

    /// <summary>Shows the controls and starts the idle countdown again; the cursor stays as it is.</summary>
    private void ShowControls()
    {
        SetControlsVisible(true);
        RestartHideTimer();
    }

    // Each key and each move of the pointer starts the wait again.
    private void RestartHideTimer()
    {
        _hideTimer.Stop();
        _hideTimer.Interval = _movesThroughControls ? RemoteHideDelay : HideDelay;
        _hideTimer.Start();
    }

    // A tap is a finger's: where the mouse rests over the mini view does not keep the controls from it.
    private void ToggleControls()
    {
        if (_areControlsVisible && Hold(countsMouseOverMiniView: false) == ControlsHold.None)
        {
            _hideTimer.Stop();
            SetControlsVisible(false);
        }
        else
        {
            ShowControls();
        }
    }

    private void HideControlsWhenIdle()
    {
        var hold = Hold();
        if (hold != ControlsHold.None)
        {
            if (hold != _loggedHold)
            {
                _loggedHold = hold;
                _autoHideLog.LogDebug("The player's controls stay up: {Hold}", hold);
            }
            // Asked again a little later, until nothing holds them any more.
            _hideTimer.Start();
            return;
        }
        _loggedHold = ControlsHold.None;
        // A remote's focus goes back to the picture, as on Silo's TV app: the arrows skip and change the volume again,
        // and OK brings the controls back with focus on Play/Pause.
        if (_movesThroughControls && HasFocusInControls())
        {
            Focus(FocusState.Programmatic);
        }
        SetControlsVisible(false);
    }

    // The bar always shows its controls. A control reached with Tab keeps them, as on the web; a remote's focus does not:
    // Silo's TV app hides them after a while without a key, focus or not. Where the pointer rests in the full player keeps
    // nothing up, as on the web: resting on the bar or at the screen's edge held them, and the cursor with them, for good.
    // The mini view keeps them while the pointer is over it: its buttons fill the small window.
    private ControlsHold Hold(bool countsMouseOverMiniView = true) => new PlayerControlsIdle
    {
        IsDocked = IsDocked,
        IsMiniView = IsMiniView,
        IsPointerOverWindow = countsMouseOverMiniView && IsMiniView && IsPointerOverWindow(),
        IsPlaying = ViewModel.IsPlaying,
        IsPaused = ViewModel.IsPaused,
        IsBuffering = ViewModel.IsBuffering,
        IsDraggingSeekBar = SeekBar.IsDragging,
        HasOpenMenu = HasOpenMenu(),
        HasKeyboardFocus = HasKeyboardFocusInControls(),
        MovesThroughControls = _movesThroughControls,
        IsPanelOpen = IsPanelOpen,
        IsEditingMarkers = ViewModel.MarkerEditor.IsEditing,
    }.Hold;

    private bool HasKeyboardFocusInControls() =>
        FocusManager.GetFocusedElement(XamlRoot) is Control { FocusState: FocusState.Keyboard } control && IsInControls(control);

    // Any focus: a menu that closes gives its button focus back as set in code, not as the keyboard's.
    private bool HasFocusInControls() => FocusManager.GetFocusedElement(XamlRoot) is Control control && IsInControls(control);

    private bool IsInControls(Control control) =>
        IsWithin(control, TopChrome) || IsWithin(control, BottomChrome) || IsWithin(control, MiniChrome) || IsWithin(control, PartyPanel);

    private static bool IsWithin(DependencyObject element, DependencyObject container)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, container))
            {
                return true;
            }
        }
        return false;
    }

    // Hidden controls take no clicks, so a click there reaches the picture. The Watch Party panel fades with them, as on
    // the web. However they hide (the idle wait, a remote's Back, a tap), the cursor goes with them over the picture.
    private void SetControlsVisible(bool visible)
    {
        _areControlsVisible = visible;
        _movesThroughControls &= visible;
        TopChrome.Opacity = BottomChrome.Opacity = MiniChrome.Opacity = visible ? 1 : 0;
        TopChrome.IsHitTestVisible = BottomChrome.IsHitTestVisible = MiniChrome.IsHitTestVisible = visible;
        ShowPartyPanel(visible);
        TopChrome.Translation = visible ? Vector3.Zero : new Vector3(0, -ChromeSlide, 0);
        BottomChrome.Translation = visible ? Vector3.Zero : new Vector3(0, ChromeSlide, 0);
        ShowCornerButton();
        LiftSubtitles();
        PlaceSkipPrompt();
        if (visible)
        {
            _cursor.ControlsShown();
        }
        else
        {
            bool isOverPicture = PointerInPage() is not null;
            _cursor.ControlsHidden(isOverPicture: isOverPicture && BlankCursor.Instance is not null);
            ApplyCursor();
            _autoHideLog.LogDebug("The player's controls hid (pointer over the picture: {OverPicture}, cursor hidden: {CursorHidden})",
                isOverPicture, _cursor.IsHidden);
        }
        WatchCursor();
    }

    // The window's Exit full screen fades with the controls, but stays on the Playing Next screen, as its Close does.
    private void ShowCornerButton()
    {
        bool shown = _areControlsVisible || ShowsNextUpScreen;
        CornerFullScreenButton.Opacity = shown ? 1 : 0;
        CornerFullScreenButton.IsHitTestVisible = shown;
    }

    // The skip pill and the next episode's card sit 28 px off the corner, and above the bar while it shows; the plan
    // change's mark with them.
    private void PlaceSkipPrompt()
    {
        double above = _areControlsVisible && BottomBar.ActualHeight > 0 ? BottomBar.ActualHeight + SkipPromptGap - SkipPrompt.Margin.Bottom : 0;
        SkipPrompt.Translation = NextUpCard.Translation = new Vector3(0, -(float)Math.Max(0, above), 0);
        PlacePlanChangeMark(Math.Max(0, above));
    }

    // Subtitles at the bottom move above the controls while they show, and back down when they fade. The bar's
    // picture has no controls over it.
    private void LiftSubtitles() =>
        ViewModel.LiftSubtitles(!IsDocked && _areControlsVisible && ActualHeight > 0 && BottomBar.ActualHeight > 0
            ? BottomBar.ActualHeight / ActualHeight
            : 0);

    // Only a real move shows the cursor and the controls: not the framework testing a still pointer again as the
    // controls come back under it after a key, nor the page moving under a still pointer as the window changes. A mouse's
    // move is measured on the screen for that; a finger's or a pen's always counts, and so does a mouse's where Windows
    // does not say where it is.
    private void OnPagePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse)
        {
            _cursor.TouchOrPenMoves();
        }
        else if (!GetCursorPos(out var at))
        {
            _cursor.Show();
        }
        else if (!_cursor.Moves(at.X, at.Y, XamlRoot?.RasterizationScale ?? 1))
        {
            // Seen again without moving, for one as a window over it went. Such events come at the mouse's report rate.
            if (_cursor.IsLookDue)
            {
                LookAtCursorAgain("the pointer was seen without moving", reassert: false);
            }
            return;
        }
        ApplyCursor();
        ShowControls();
    }

    private void OnWindowActivatedForCursor(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState != WindowActivationState.Deactivated)
        {
            LookAtCursorAgain("the window was activated", reassert: true);
        }
    }

    // Many times during a resize.
    private void OnXamlRootChangedForCursor(XamlRoot sender, XamlRootChangedEventArgs e)
    {
        if (_cursor.IsLookDue)
        {
            LookAtCursorAgain("the window changed", reassert: false);
        }
    }

    private void OnWindowModeChangedForCursor(object? sender, EventArgs e) => LookAtCursorAgain("the window's mode changed", reassert: true);

    // While the controls are hidden: a cursor they left showing, because another window was under the pointer as they hid,
    // goes once the pointer is over the picture. Windows is asked where the pointer is only while the cursor waits for
    // that. With reassert, on the window's own rare changes, a hidden cursor is set again too.
    private void LookAtCursorAgain(string occasion, bool reassert)
    {
        if (!IsLoaded)
        {
            _cursorTimer.Stop();
            return;
        }
        if (_areControlsVisible)
        {
            return;
        }
        bool hidNow = _cursor.WaitsForPicture
            && _cursor.LooksAgain(isOverPicture: BlankCursor.Instance is not null && PointerInPage() is not null);
        if (hidNow)
        {
            _autoHideLog.LogDebug("The cursor the player's controls left showing hid: {Occasion}", occasion);
        }
        if (hidNow || reassert)
        {
            ApplyCursor(reassert);
        }
        WatchCursor();
    }

    // Nothing needs to tell the page that a window over the pointer went, so while the hidden controls leave the cursor
    // showing off the picture, it is looked at again now and then. Only then: the rest of the time events are enough.
    private void WatchCursor()
    {
        if (!_cursor.WaitsForPicture || BlankCursor.Instance is null)
        {
            _cursorTimer.Stop();
        }
        else if (!_cursorTimer.IsRunning)
        {
            _cursorTimer.Start();
        }
    }

    /// <summary>
    /// The pointer in page coordinates while it is over the picture, else null. Asked from Windows rather than
    /// followed through XAML's enter and exit events: those bubble up from the elements inside, and the title
    /// bar's drag area, where Windows draws the cursor itself, sends none.
    /// </summary>
    private Point? PointerInPage()
    {
        if (XamlRoot is not { } root || !PointerOverWindow(out var cursor, out nint window) || !ScreenToClient(window, ref cursor))
        {
            return null;
        }
        var origin = TransformToVisual(null).TransformPoint(default);
        var point = new Point(cursor.X / root.RasterizationScale - origin.X, cursor.Y / root.RasterizationScale - origin.Y);
        bool inPage = point.X >= 0 && point.Y >= 0 && point.X < ActualWidth && point.Y < ActualHeight;
        bool inDragArea = !IsFullScreen && point.Y < TitleBarHeight;
        return inPage && !inDragArea ? point : null;
    }

    private bool IsPointerOverWindow() => PointerOverWindow(out _, out _);

    // Where the pointer is on the screen, while it is over this window rather than another one in front of it.
    private bool PointerOverWindow(out NativePoint cursor, out nint window)
    {
        window = 0;
        if (_appWindow is null || !GetCursorPos(out cursor))
        {
            cursor = default;
            return false;
        }
        window = Win32Interop.GetWindowFromWindowId(_appWindow.Id);
        return GetAncestor(WindowFromPoint(cursor), GaRoot) == window;
    }

    // Over WinUI content the cursor is the one of the element under the pointer, which WinUI's input stack sets:
    // ShowCursor does not reach it. The page's cursor goes blank instead; the elements on the picture have none of
    // their own and take it. It is only the page's, so the title bar and other windows keep theirs, and the pointer
    // coming back moves, which shows the controls and the arrow again. Set when the rule's answer changes, and with
    // reassert the blank one again, rather than trusting that it is still the one shown.
    private void ApplyCursor(bool reassert = false)
    {
        if (!_cursor.IsHidden)
        {
            if (_isCursorHidden)
            {
                _isCursorHidden = false;
                // The arrow, not null: null leaves the shown cursor as it is until the pointer reaches another element.
                ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
            }
            return;
        }
        if (BlankCursor.Instance is not { } blank)
        {
            return;
        }
        if (_isCursorHidden)
        {
            if (!reassert)
            {
                return;
            }
            // The same cursor set again could count as no change; null on the way leaves the shown one as it is.
            ProtectedCursor = null;
        }
        _isCursorHidden = true;
        ProtectedCursor = blank;
    }
}
