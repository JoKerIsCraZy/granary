using System.Numerics;
using Granary.App.Controls;
using Granary.Core.ViewModels.Player;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Granary.App.Views;

// The controls fade out after a few seconds of playback without pointer movement or a key, and stay while there is
// something to read or do in them. The cursor goes with them over the picture and comes back only with the mouse
// (PlayerCursor).
public sealed partial class PlayerPage
{
    private static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(3);

    // With a remote's focus in the controls they wait as long as Silo's TV app's (CONTROLS_AUTO_HIDE_MS).
    private static readonly TimeSpan RemoteHideDelay = TimeSpan.FromSeconds(5);

    private const float ChromeSlide = 8;

    // Between the skip pill and the seek bar's row, while the controls show.
    private const double SkipPromptGap = 12;

    private DispatcherQueueTimer _hideTimer = null!;
    private bool _areControlsVisible = true;

    // Whether the cursor shows, and whether the page's cursor is the blank one now.
    private readonly PlayerCursor _cursor = new();
    private bool _isCursorHidden;

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
        PointerMoved += OnPagePointerMoved;
    }

    private void StopAutoHide()
    {
        _hideTimer.Stop();
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

    private void ToggleControls()
    {
        if (_areControlsVisible && !KeepsControls())
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
        if (KeepsControls())
        {
            // Asked again a little later, until nothing holds them any more.
            _hideTimer.Start();
            return;
        }
        // A remote's focus goes back to the picture, as on Silo's TV app: the arrows skip and change the volume again,
        // and OK brings the controls back with focus on Play/Pause.
        if (_movesThroughControls && HasFocusInControls())
        {
            Focus(FocusState.Programmatic);
        }
        SetControlsVisible(false);
    }

    // The bar always shows its controls, and the pointer over the pages stays. A control reached with Tab keeps them, as
    // on the web; a remote's focus does not: Silo's TV app hides them after a while without a key, focus or not. The
    // pointer resting on them keeps them only while it shows.
    private bool KeepsControls() =>
        IsDocked || !ViewModel.IsPlaying || ViewModel.IsPaused || ViewModel.IsBuffering
        || SeekBar.IsDragging || _cursor.HoldsControls(IsPointerOverControls()) || HasOpenMenu()
        || (HasKeyboardFocusInControls() && !_movesThroughControls) || IsPanelOpen || ViewModel.MarkerEditor.IsEditing;

    private bool IsPointerOverControls() =>
        PointerInPage() is { } point && (Contains(BottomBar, point) || Contains(ExitButton, point) || Contains(MinimizeButton, point)
            || Contains(CornerFullScreenButton, point)
            || (PartyPanel is { Visibility: Visibility.Visible, IsHitTestVisible: true } && Contains(PartyPanel, point))
            || (IsMiniView && (Contains(MiniPlayPauseButton, point) || Contains(LeaveMiniViewButton, point))));

    private bool Contains(FrameworkElement element, Point point) =>
        element.ActualWidth > 0
        && element.TransformToVisual(this).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight)).Contains(point);

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
        if (!visible)
        {
            _cursor.ControlsHidden(isOverPicture: BlankCursor.Instance is not null && PointerInPage() is not null);
            ApplyCursor();
        }
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
    // controls come back under it after a key.
    private void OnPagePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(this).Position;
        if (!_cursor.Moves(position.X, position.Y))
        {
            return;
        }
        ApplyCursor();
        ShowControls();
    }

    /// <summary>
    /// The pointer in page coordinates while it is over the picture, else null. Asked from Windows rather than
    /// followed through XAML's enter and exit events: those bubble up from the elements inside, and the title
    /// bar's drag area, where Windows draws the cursor itself, sends none.
    /// </summary>
    private Point? PointerInPage()
    {
        if (_appWindow is null || XamlRoot is not { } root || !GetCursorPos(out var cursor))
        {
            return null;
        }
        nint window = Win32Interop.GetWindowFromWindowId(_appWindow.Id);
        if (GetAncestor(WindowFromPoint(cursor), GaRoot) != window || !ScreenToClient(window, ref cursor))
        {
            return null;
        }
        var origin = TransformToVisual(null).TransformPoint(default);
        var point = new Point(cursor.X / root.RasterizationScale - origin.X, cursor.Y / root.RasterizationScale - origin.Y);
        bool inPage = point.X >= 0 && point.Y >= 0 && point.X < ActualWidth && point.Y < ActualHeight;
        bool inDragArea = !IsFullScreen && point.Y < TitleBarHeight;
        return inPage && !inDragArea ? point : null;
    }

    // Over WinUI content the cursor is the one of the element under the pointer, which WinUI's input stack sets:
    // ShowCursor does not reach it. The page's cursor goes blank instead; the elements on the picture have none of
    // their own and take it. It is only the page's, so the title bar and other windows keep theirs, and the pointer
    // coming back moves, which shows the controls and the arrow again. Set only when the rule's answer changes.
    private void ApplyCursor()
    {
        if (_cursor.IsHidden == _isCursorHidden)
        {
            return;
        }
        if (!_cursor.IsHidden)
        {
            _isCursorHidden = false;
            // The arrow, not null: null leaves the shown cursor as it is until the pointer reaches another element.
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        }
        else if (BlankCursor.Instance is { } blank)
        {
            _isCursorHidden = true;
            ProtectedCursor = blank;
        }
    }
}
