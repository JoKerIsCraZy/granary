using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Granary.App.Controls;

/// <summary>
/// The player's seek bar. It follows <see cref="Position"/> and <see cref="Duration"/> (seconds) and raises
/// <see cref="SeekRequested"/> only for the user: on a click, when a drag is released (not while it moves), and
/// for keys (Left and Right by the skip steps, with Shift by 5 s, Home, End). Setting <see cref="Position"/>
/// never raises it. Disabled, it only shows the position.
/// </summary>
public sealed partial class SeekBar : UserControl
{
    public static readonly DependencyProperty PositionProperty = Register(nameof(Position), 0.0);

    public static readonly DependencyProperty DurationProperty = Register(nameof(Duration), 0.0);

    public static readonly DependencyProperty BackStepSecondsProperty = Register(nameof(BackStepSeconds), 10.0);

    public static readonly DependencyProperty ForwardStepSecondsProperty = Register(nameof(ForwardStepSeconds), 30.0);

    private const double NudgeSeconds = 5;
    private const double ThumbRadius = 7;
    private const double TipGap = 12;

    // A seek shows in the position only once the player got there; until then the bar keeps the target
    // instead of jumping back to where playback was.
    private const long PendingHoldMilliseconds = 2000;
    private const double PendingToleranceSeconds = 1.5;

    private static readonly Vector3 ActiveTrackScale = new(1, 5f / 3, 1);

    private bool _isPointerOver;
    private double? _dragSeconds;
    private double? _pendingSeconds;
    private long _pendingSince;

    public SeekBar()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Render();
        IsEnabledChanged += (_, _) =>
        {
            if (!IsEnabled)
            {
                CancelDrag();
            }
            Render();
        };
    }

    /// <summary>Raised with the target in seconds when the user picks a time.</summary>
    public event TypedEventHandler<SeekBar, double>? SeekRequested;

    /// <summary>Playback position in seconds.</summary>
    public double Position
    {
        get => (double)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    /// <summary>Length in seconds; 0 while unknown, which leaves the bar empty and inert.</summary>
    public double Duration
    {
        get => (double)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    /// <summary>Seconds the Left key goes back.</summary>
    public double BackStepSeconds
    {
        get => (double)GetValue(BackStepSecondsProperty);
        set => SetValue(BackStepSecondsProperty, value);
    }

    /// <summary>Seconds the Right key goes forward.</summary>
    public double ForwardStepSeconds
    {
        get => (double)GetValue(ForwardStepSecondsProperty);
        set => SetValue(ForwardStepSecondsProperty, value);
    }

    /// <summary>A drag is in progress; the page keeps its controls up meanwhile.</summary>
    public bool IsDragging => _dragSeconds is not null;

    /// <summary>The time the bar shows: the drag, a seek still on its way, or the position.</summary>
    public double DisplayedSeconds => _dragSeconds ?? PendingSeconds() ?? Position;

    internal bool CanSeek => IsEnabled && Duration > 0;

    /// <summary>Seeks to <paramref name="seconds"/> as the user (keys, screen readers).</summary>
    internal void RequestSeek(double seconds)
    {
        if (!CanSeek)
        {
            return;
        }
        double previous = DisplayedSeconds;
        double target = Math.Clamp(seconds, 0, Duration);
        _pendingSeconds = target;
        _pendingSince = Environment.TickCount64;
        Render();
        SeekRequested?.Invoke(this, target);
        (FrameworkElementAutomationPeer.FromElement(this) as SeekBarAutomationPeer)?.RaiseValueChanged(previous, target);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SeekBarAutomationPeer(this);

    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        base.OnPointerEntered(e);
        _isPointerOver = true;
        double x = e.GetCurrentPoint(Track).Position.X;
        ShowTip(_dragSeconds ?? SecondsAt(x), x);
        Render();
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        double x = e.GetCurrentPoint(Track).Position.X;
        double seconds = SecondsAt(x);
        if (_dragSeconds is not null)
        {
            _dragSeconds = seconds;
            Render();
        }
        ShowTip(seconds, x);
    }

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        base.OnPointerExited(e);
        _isPointerOver = false;
        if (_dragSeconds is null)
        {
            HideTip();
        }
        Render();
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(Track);
        // Touch and pen contacts report as the left button.
        if (!CanSeek || !point.Properties.IsLeftButtonPressed || !CapturePointer(e.Pointer))
        {
            return;
        }
        e.Handled = true;
        Focus(FocusState.Pointer);
        _dragSeconds = SecondsAt(point.Position.X);
        ShowTip(_dragSeconds.Value, point.Position.X);
        Render();
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragSeconds is null)
        {
            return;
        }
        e.Handled = true;
        double seconds = SecondsAt(e.GetCurrentPoint(Track).Position.X);
        // Cleared first: releasing the capture raises PointerCaptureLost, which cancels a drag.
        _dragSeconds = null;
        ReleasePointerCapture(e.Pointer);
        if (!_isPointerOver)
        {
            HideTip();
        }
        RequestSeek(seconds);
    }

    // The capture can go without a release (another window took the input): that drag does not seek.
    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        CancelDrag();
    }

    protected override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        base.OnPointerCanceled(e);
        CancelDrag();
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        // Ctrl and Alt combinations stay with the app (Alt+Left is Back).
        if (CanSeek && !IsDown(VirtualKey.Control) && !IsDown(VirtualKey.Menu))
        {
            bool nudge = IsDown(VirtualKey.Shift);
            double? target = e.Key switch
            {
                VirtualKey.Left => DisplayedSeconds - (nudge ? NudgeSeconds : BackStepSeconds),
                VirtualKey.Right => DisplayedSeconds + (nudge ? NudgeSeconds : ForwardStepSeconds),
                VirtualKey.Home => 0,
                VirtualKey.End => Duration,
                _ => null,
            };
            if (target is { } seconds)
            {
                e.Handled = true;
                RequestSeek(seconds);
                return;
            }
        }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(RoutedEventArgs e)
    {
        base.OnGotFocus(e);
        Render();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        Render();
    }

    private static DependencyProperty Register(string name, double defaultValue) =>
        DependencyProperty.Register(name, typeof(double), typeof(SeekBar),
            new PropertyMetadata(defaultValue, (sender, _) => ((SeekBar)sender).OnTimelineChanged()));

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnTimelineChanged()
    {
        if (PendingSeconds() is null)
        {
            _pendingSeconds = null;
        }
        Render();
    }

    private double? PendingSeconds()
    {
        if (_pendingSeconds is not { } pending)
        {
            return null;
        }
        bool arrived = Math.Abs(Position - pending) <= PendingToleranceSeconds;
        bool expired = Environment.TickCount64 - _pendingSince > PendingHoldMilliseconds;
        return arrived || expired ? null : pending;
    }

    private double SecondsAt(double x)
    {
        double width = Track.ActualWidth;
        return width > 0 && Duration > 0 ? Math.Clamp(x / width, 0, 1) * Duration : 0;
    }

    private void CancelDrag()
    {
        if (_dragSeconds is null)
        {
            return;
        }
        _dragSeconds = null;
        if (!_isPointerOver)
        {
            HideTip();
        }
        Render();
    }

    private void ShowTip(double seconds, double x)
    {
        if (!CanSeek)
        {
            HideTip();
            return;
        }
        TipText.Text = PlayerTime.Format(seconds, Duration);
        Tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = Tip.DesiredSize.Width;
        Canvas.SetLeft(Tip, Math.Clamp(x - width / 2, 0, Math.Max(0, Track.ActualWidth - width)));
        Canvas.SetTop(Tip, -(Tip.Height + TipGap));
        Tip.Opacity = 1;
    }

    private void HideTip() => Tip.Opacity = 0;

    private void Render()
    {
        double width = Track.ActualWidth;
        double fraction = Duration > 0 ? Math.Clamp(DisplayedSeconds / Duration, 0, 1) : 0;
        Progress.Width = fraction * width;
        Thumb.Translation = new Vector3((float)(fraction * width - ThumbRadius), 0, 0);
        bool isActive = CanSeek && (_isPointerOver || IsDragging || FocusState == FocusState.Keyboard);
        Thumb.Opacity = isActive ? 1 : 0;
        Track.Scale = isActive ? ActiveTrackScale : Vector3.One;
        Track.Opacity = IsEnabled ? 1 : 0.5;
    }
}
