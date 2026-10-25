using System.Numerics;
using Microsoft.UI;
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

    // While the cursor is hidden, how often to look whether the pointer left the picture without moving over it
    // (into the title bar's drag area, or another window): XAML reports neither.
    private static readonly TimeSpan CursorCheckInterval = TimeSpan.FromMilliseconds(200);

    private const float ChromeSlide = 8;

    // Between the skip pill and the seek bar's row, while the controls show.
    private const double SkipPromptGap = 12;

    private DispatcherQueueTimer _hideTimer = null!;
    private DispatcherQueueTimer _cursorTimer = null!;
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
        _cursorTimer = DispatcherQueue.CreateTimer();
        _cursorTimer.Interval = CursorCheckInterval;
        _cursorTimer.Tick += (_, _) =>
        {
            if (PointerInPage() is null)
            {
                ShowCursor();
            }
        };
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

    private bool KeepsControls() =>
        !ViewModel.IsPlaying || ViewModel.IsPaused || ViewModel.IsBuffering
        || SeekBar.IsDragging || IsPointerOverControls() || HasOpenMenu() || HasKeyboardFocusInControls();

    private bool IsPointerOverControls() =>
        PointerInPage() is { } point && (Contains(BottomBar, point) || Contains(ExitButton, point));

    private bool Contains(FrameworkElement element, Point point) =>
        element.ActualWidth > 0
        && element.TransformToVisual(this).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight)).Contains(point);

    private bool HasKeyboardFocusInControls() =>
        FocusManager.GetFocusedElement(XamlRoot) is Control { FocusState: FocusState.Keyboard } control
        && (IsWithin(control, TopChrome) || IsWithin(control, BottomChrome));

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
        TopChrome.Opacity = BottomChrome.Opacity = visible ? 1 : 0;
        TopChrome.IsHitTestVisible = BottomChrome.IsHitTestVisible = visible;
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

    // Subtitles at the bottom move above the controls while they show, and back down when they fade.
    private void LiftSubtitles() =>
        ViewModel.LiftSubtitles(_areControlsVisible && ActualHeight > 0 && BottomBar.ActualHeight > 0
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

    // XAML has no hidden cursor (a custom one needs a cursor resource in the executable), so this uses the
    // thread's cursor display count, balanced by _isCursorHidden. The count also applies to the title bar, so
    // the cursor comes back as soon as the pointer is off the picture.
    private void HideCursor()
    {
        if (!_isCursorHidden)
        {
            _isCursorHidden = true;
            _ = NativeShowCursor(false);
            _cursorTimer.Start();
        }
    }

    private void ShowCursor()
    {
        if (_isCursorHidden)
        {
            _isCursorHidden = false;
            _ = NativeShowCursor(true);
            _cursorTimer.Stop();
        }
    }
}
