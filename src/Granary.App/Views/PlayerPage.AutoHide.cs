using System.Numerics;
using Granary.App.Controls;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Granary.App.Views;

// The controls and the cursor fade out after a few seconds of playback without pointer movement, and stay
// while there is something to read or do in them.
public sealed partial class PlayerPage
{
    private static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(3);

    // Moves this small are jitter, or the framework testing a still pointer again while the controls fade.
    private const double PointerMoveThreshold = 2;

    private const float ChromeSlide = 8;

    // Between the skip pill and the seek bar's row, while the controls show.
    private const double SkipPromptGap = 12;

    private DispatcherQueueTimer _hideTimer = null!;
    private Point? _lastPointer;
    private bool _areControlsVisible = true;
    private bool _isCursorHidden;

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
        _hideTimer.Tick += (_, _) => HideControlsWhenIdle();
        PointerMoved += OnPagePointerMoved;
    }

    private void StopAutoHide()
    {
        _hideTimer.Stop();
        ShowCursor();
    }

    /// <summary>Shows the controls and starts the idle countdown again.</summary>
    private void ShowControls()
    {
        SetControlsVisible(true);
        ShowCursor();
        _hideTimer.Stop();
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
        SetControlsVisible(false);
        if (PointerInPage() is not null)
        {
            HideCursor();
        }
    }

    // The bar always shows its controls, and the pointer over the pages stays.
    private bool KeepsControls() =>
        IsDocked || !ViewModel.IsPlaying || ViewModel.IsPaused || ViewModel.IsBuffering
        || SeekBar.IsDragging || IsPointerOverControls() || HasOpenMenu() || HasKeyboardFocusInControls() || IsPanelOpen
        || ViewModel.MarkerEditor.IsEditing;

    private bool IsPointerOverControls() =>
        PointerInPage() is { } point && (Contains(BottomBar, point) || Contains(ExitButton, point) || Contains(MinimizeButton, point)
            || (IsMiniView && (Contains(MiniPlayPauseButton, point) || Contains(LeaveMiniViewButton, point))));

    private bool Contains(FrameworkElement element, Point point) =>
        element.ActualWidth > 0
        && element.TransformToVisual(this).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight)).Contains(point);

    private bool HasKeyboardFocusInControls() =>
        FocusManager.GetFocusedElement(XamlRoot) is Control { FocusState: FocusState.Keyboard } control
        && (IsWithin(control, TopChrome) || IsWithin(control, BottomChrome) || IsWithin(control, MiniChrome));

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

    // Hidden controls take no clicks, so a click there reaches the picture.
    private void SetControlsVisible(bool visible)
    {
        _areControlsVisible = visible;
        TopChrome.Opacity = BottomChrome.Opacity = MiniChrome.Opacity = visible ? 1 : 0;
        TopChrome.IsHitTestVisible = BottomChrome.IsHitTestVisible = MiniChrome.IsHitTestVisible = visible;
        TopChrome.Translation = visible ? Vector3.Zero : new Vector3(0, -ChromeSlide, 0);
        BottomChrome.Translation = visible ? Vector3.Zero : new Vector3(0, ChromeSlide, 0);
        LiftSubtitles();
        PlaceSkipPrompt();
    }

    // The skip pill and the next episode's card sit 28 px off the corner, and above the bar while it shows.
    private void PlaceSkipPrompt()
    {
        double above = _areControlsVisible && BottomBar.ActualHeight > 0 ? BottomBar.ActualHeight + SkipPromptGap - SkipPrompt.Margin.Bottom : 0;
        SkipPrompt.Translation = NextUpCard.Translation = new Vector3(0, -(float)Math.Max(0, above), 0);
    }

    // Subtitles at the bottom move above the controls while they show, and back down when they fade. The bar's
    // picture has no controls over it.
    private void LiftSubtitles() =>
        ViewModel.LiftSubtitles(!IsDocked && _areControlsVisible && ActualHeight > 0 && BottomBar.ActualHeight > 0
            ? BottomBar.ActualHeight / ActualHeight
            : 0);

    private void OnPagePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(this).Position;
        if (_lastPointer is { } last
            && Math.Abs(position.X - last.X) < PointerMoveThreshold
            && Math.Abs(position.Y - last.Y) < PointerMoveThreshold)
        {
            return;
        }
        _lastPointer = position;
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
    // coming back moves, which shows the controls and the arrow again.
    private void HideCursor()
    {
        if (!_isCursorHidden && BlankCursor.Instance is { } blank)
        {
            _isCursorHidden = true;
            ProtectedCursor = blank;
        }
    }

    private void ShowCursor()
    {
        if (_isCursorHidden)
        {
            _isCursorHidden = false;
            // The arrow, not null: null leaves the shown cursor as it is until the pointer reaches another element.
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        }
    }
}
