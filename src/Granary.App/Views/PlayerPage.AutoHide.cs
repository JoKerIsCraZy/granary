using System.Numerics;
using Granary.App.Controls;
using Granary.App.Interop;
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
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
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

    // How often a cursor the hidden controls left showing off the picture is looked at again, and a hidden one is checked
    // for Windows drawing a visible cursor over it (CheckShownCursor): often enough that an arrow is gone before it is noticed.
    private static readonly TimeSpan CursorLookInterval = TimeSpan.FromMilliseconds(250);

    // How many times Windows drawing a visible cursor over a hidden one is logged for one hide.
    private const int CursorLogLimit = 5;

    private const float ChromeSlide = 8;

    // Between the skip pill and the seek bar's row, while the controls show.
    private const double SkipPromptGap = 12;

    private DispatcherQueueTimer _hideTimer = null!;
    private DispatcherQueueTimer _cursorTimer = null!;
    private bool _areControlsVisible = true;

    // Whether the cursor shows, and whether the page's cursor is the blank one now.
    private readonly PlayerCursor _cursor = new(TimeProvider.System);
    private bool _isCursorHidden;
    private CursorWindowHook? _cursorWindowHook;
    private int _cursorsLoggedWhileHidden;
    private int _answeredAsCursorHid;

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
        _cursorWindowHook?.Dispose();
        _cursorWindowHook = null;
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
            // How far the mouse went while the cursor showed tells a hand (hundreds of pixels) from a knock (a few).
            double travelled = _cursor.Travelled;
            // The cursor hides only where Windows says where the pointer is: moves then count from there, never from a
            // place the page saw it at earlier. Else it is left showing and looked at again.
            var rests = ScreenPointer();
            bool isOverPicture = rests is not null && PointerInPage() is not null;
            _cursor.ControlsHidden(isOverPicture: isOverPicture && BlankCursor.Instance is not null, restsAt: rests);
            ApplyCursor();
            _autoHideLog.LogDebug(
                "The player's controls hid (pointer over the picture: {OverPicture}, cursor hidden: {CursorHidden}, the mouse went {Travelled:0} px while it showed)",
                isOverPicture, _cursor.IsHidden, travelled);
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
        if (!IsRealMove(e.Pointer.PointerDeviceType))
        {
            // Seen again without moving, for one as a window over it went. Such events come at the mouse's report rate.
            if (_cursor.IsLookDue)
            {
                LookAtCursorAgain("the pointer was seen without moving", reassert: false);
            }
            return;
        }
        // Every real move, a measured one or not, shows the arrow and the controls.
        ApplyCursor();
        ShowControls();
    }

    // The log says what brought a hidden cursor back: how far the mouse went and how long it had not been seen (a few
    // pixels are a knock or the sensor's noise, a long way is a hand), or that it was no mouse at all.
    private bool IsRealMove(PointerDeviceType device)
    {
        bool wasHidden = _cursor.IsHidden;
        if (device != PointerDeviceType.Mouse)
        {
            _cursor.TouchOrPenMoves();
            if (wasHidden)
            {
                _autoHideLog.LogDebug("The player's cursor came back: a {Device} moved", device);
            }
            return true;
        }
        if (!GetCursorPos(out var at))
        {
            _cursor.Show();
            if (wasHidden)
            {
                _autoHideLog.LogDebug("The player's cursor came back: Windows did not say where the mouse is");
            }
            return true;
        }
        if (!_cursor.Moves(at.X, at.Y, XamlRoot?.RasterizationScale ?? 1))
        {
            return false;
        }
        if (wasHidden)
        {
            _autoHideLog.LogDebug("The player's cursor came back: the mouse moved {Distance:0.#} px, seen {Seconds:0.#} s before",
                _cursor.LastMove.Distance, _cursor.LastMove.SinceSeen.TotalSeconds);
        }
        return true;
    }

    // Where the pointer is on the screen, when Windows says.
    private static (double X, double Y)? ScreenPointer() => GetCursorPos(out var at) ? (at.X, at.Y) : null;

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
    // that. With reassert, on the window's own rare changes, a hidden cursor is set again too, also while the controls
    // show after a key (F, P): a mode change can leave the page's cursor reset.
    private void LookAtCursorAgain(string occasion, bool reassert)
    {
        if (!IsLoaded)
        {
            _cursorTimer.Stop();
            return;
        }
        var rests = ScreenPointer();
        bool hidNow = !_areControlsVisible && _cursor.WaitsForPicture
            && _cursor.LooksAgain(isOverPicture: BlankCursor.Instance is not null && rests is not null && PointerInPage() is not null, restsAt: rests);
        if (hidNow)
        {
            _autoHideLog.LogDebug("The cursor the player's controls left showing hid: {Occasion}", occasion);
        }
        if (hidNow || reassert)
        {
            ApplyCursor(reassert);
        }
        // Not in the look that hid it: Windows is asked for the hidden one only after this.
        if (!hidNow)
        {
            CheckShownCursor();
        }
        WatchCursor();
    }

    // While the player's cursor is hidden, Granary's windows answer Windows' question for it themselves
    // (HookCursorRequests). This look is what is left for a cursor set past that question: it asks once more, and the log
    // says what Windows drew, whether asking helped, and whose windows were under the pointer and in front, a few times
    // for each time the cursor hid.
    private void CheckShownCursor()
    {
        // Not over a dialog, a menu or a tooltip: their cursor is theirs (HookCursorRequests).
        if (!_isCursorHidden || HasOpenPopup() || PointerInPage() is null || OwnWindowUnderPointer() is not (var under and not 0))
        {
            return;
        }
        HookCursorRequests(hidden: true);
        if (!ShowsOtherCursor())
        {
            return;
        }
        string shape = ShowsArrow() ? "its arrow" : "a cursor of its own";
        AskForCursor(under);
        if (ShowsOtherCursor())
        {
            _ = SetCursor(BlankCursor.Handle);
        }
        if (_cursorsLoggedWhileHidden < CursorLogLimit)
        {
            _cursorsLoggedWhileHidden++;
            // Whose window it was says why a cursor could not be taken back: Windows shows a thread's cursor only while
            // that thread has the mouse's input. Window classes only, never a title.
            _autoHideLog.LogDebug(
                "Windows drew {Shape} over the picture while the player's cursor is hidden (restored: {Restored}; under the pointer: {Under}; in front: {Front}; requests answered since it hid: {Answered})",
                shape, !ShowsOtherCursor(), DescribeWindow(under, withClass: true), DescribeWindow(GetForegroundWindow(), withClass: false),
                (_cursorWindowHook?.Answered ?? 0) - _answeredAsCursorHid);
        }
    }

    private static bool ShowsOtherCursor()
    {
        var info = new CursorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref info) && (info.Flags & CursorShowing) != 0
            && info.Cursor != 0 && info.Cursor != BlankCursor.Handle;
    }

    private bool HasOpenPopup() =>
        XamlRoot is { } root && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count > 0;

    private static bool ShowsArrow()
    {
        var info = new CursorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref info) && info.Cursor != 0 && info.Cursor == LoadCursorW(0, ArrowCursorId);
    }

    // A window as the log may name it: whose it is, and for one of Granary's own its class, which says which part of the
    // window it is ("Microsoft.UI.Content.DesktopChildSiteBridge"). Another program's window is not named: which program
    // the viewer has in front is theirs.
    private static unsafe string DescribeWindow(nint window, bool withClass)
    {
        if (window == 0)
        {
            return "none";
        }
        uint thread = GetWindowThreadProcessId(window, out uint process);
        if (process != (uint)Environment.ProcessId)
        {
            return "another program's";
        }
        string whose = thread == GetCurrentThreadId() ? "this thread's" : "another thread's of Granary";
        if (!withClass)
        {
            return whose;
        }
        char* name = stackalloc char[128];
        int length = GetClassNameW(window, name, 128);
        return (length > 0 ? new string(name, 0, length) : "?") + ", " + whose;
    }

    // While the cursor is hidden, every window of Granary's answers Windows' question for its cursor with the hidden one
    // while the pointer is over the picture (CursorWindowHook): all of them, so it does not matter which one Windows asks,
    // and from the moment the cursor hides, wherever the pointer is then. The hook itself looks where the pointer is.
    // Not while a dialog, a menu or a tooltip is open: those lie over the picture in the same window, the page hears no
    // pointer move over a dialog, and so nothing would bring the cursor back on it. WinUI answers then, as before.
    private void HookCursorRequests(bool hidden)
    {
        if (!hidden || BlankCursor.Handle == 0 || _appWindow is null)
        {
            _cursorWindowHook?.Watch(0);
            return;
        }
        _cursorWindowHook ??= new CursorWindowHook(BlankCursor.Handle,
            () => IsLoaded && _isCursorHidden && !HasOpenPopup() && PointerInPage() is not null);
        _cursorWindowHook.Watch(Win32Interop.GetWindowFromWindowId(_appWindow.Id));
    }

    // Nothing needs to tell the page that a window over the pointer went, so while the hidden controls leave the cursor
    // showing off the picture, it is looked at again now and then; and while it is hidden, that Windows still draws the
    // hidden one (CheckShownCursor). The rest of the time events are enough.
    private void WatchCursor()
    {
        HookCursorRequests(_isCursorHidden);
        if (!(_cursor.WaitsForPicture || _isCursorHidden) || BlankCursor.Instance is null)
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
                _cursorWindowHook?.Watch(0);
                // The arrow, not null: null leaves the shown cursor as it is until the pointer reaches another element.
                ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
                ShowCursorNow(hidden: false);
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
        else
        {
            // A new hide: Windows drawing its arrow is logged for it afresh, not for the hidden cursor set anew.
            _cursorsLoggedWhileHidden = 0;
            _answeredAsCursorHid = _cursorWindowHook?.Answered ?? 0;
        }
        _isCursorHidden = true;
        ProtectedCursor = blank;
        HookCursorRequests(hidden: true);
        ShowCursorNow(hidden: true);
    }

    // WinUI hands the page's new cursor to the window at once, but Windows draws it only when it next asks the window under
    // the pointer which cursor to draw, as the mouse moves or a button goes down. A mouse resting on the picture kept the
    // arrow until then, for as long as nobody touched it. So the window is asked now, as a move would ask it, once this
    // change is through and only while the pointer is still over it.
    private void ShowCursorNow(bool hidden)
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            bool asked = AskForCursor(OwnWindowUnderPointer());
            _autoHideLog.LogDebug("The player's cursor is {Cursor} (shown at once: {Asked})", hidden ? "hidden" : "the arrow", asked);
            // Should the window have answered with its arrow all the same, the hidden cursor is set at once, not at the
            // next look.
            CheckShownCursor();
        });
    }

    // The window right under the pointer while it is part of this one, else 0: the question goes to no other app's window,
    // which could be one that does not answer.
    private nint OwnWindowUnderPointer()
    {
        if (_appWindow is null || !GetCursorPos(out var cursor))
        {
            return 0;
        }
        nint under = WindowFromPoint(cursor);
        return under != 0 && GetAncestor(under, GaRoot) == Win32Interop.GetWindowFromWindowId(_appWindow.Id) ? under : 0;
    }

    private static bool AskForCursor(nint window)
    {
        if (window == 0)
        {
            return false;
        }
        _ = SendMessageW(window, WmSetCursor, window, CursorRequestForMove);
        return true;
    }
}
