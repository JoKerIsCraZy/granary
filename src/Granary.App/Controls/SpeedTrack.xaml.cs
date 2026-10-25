using Granary.Core.ViewModels.Player;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Granary.App.Controls;

/// <summary>
/// The playback speed as stops (<see cref="PlaybackSpeeds.Stops"/>). <see cref="Speed"/> shows where it is; a pick by
/// pointer or keys raises <see cref="SpeedPicked"/> with the stop's speed, never for a change of <see cref="Speed"/>.
/// </summary>
public sealed partial class SpeedTrack : UserControl
{
    public static readonly DependencyProperty SpeedProperty = DependencyProperty.Register(nameof(Speed), typeof(double), typeof(SpeedTrack),
        new PropertyMetadata(1.0, (sender, _) => ((SpeedTrack)sender).Render()));

    private const double ThumbSize = 14;
    private const double TrackTop = 16;
    private static readonly HashSet<double> Named = [0.5, 1, 2, 3];
    private static readonly int NormalStop = PlaybackSpeeds.NearestStop(1);

    private readonly Rectangle _fill = new() { Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = new SolidColorBrush(Colors.White) };
    private readonly Ellipse _thumb = new() { Width = ThumbSize, Height = ThumbSize, Fill = new SolidColorBrush(Colors.White) };
    private readonly List<Rectangle> _ticks = [];
    private readonly List<TextBlock> _labels = [];

    public SpeedTrack()
    {
        InitializeComponent();
        foreach (double speed in PlaybackSpeeds.Stops)
        {
            bool normal = Math.Abs(speed - 1) < 0.001;
            var tick = new Rectangle
            {
                Width = 1,
                Height = normal ? 12 : 8,
                Fill = new SolidColorBrush(normal ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)),
            };
            _ticks.Add(tick);
            Marks.Children.Add(tick);
            if (Named.Contains(speed))
            {
                var label = new TextBlock { Text = PlaybackSpeeds.Format(speed), FontSize = 10, Tag = speed, Foreground = Muted };
                _labels.Add(label);
                Labels.Children.Add(label);
            }
        }
        Marks.Children.Add(_fill);
        Marks.Children.Add(_thumb);
        Track.SizeChanged += (_, _) => Render();
        Track.PointerPressed += OnTrackPressed;
        Track.PointerMoved += OnTrackMoved;
        Track.PointerReleased += (_, e) => Track.ReleasePointerCapture(e.Pointer);
        Loaded += (_, _) => Render();
    }

    /// <summary>Raised with the stop's speed when the viewer picks one.</summary>
    public event TypedEventHandler<SpeedTrack, double>? SpeedPicked;

    public double Speed
    {
        get => (double)GetValue(SpeedProperty);
        set => SetValue(SpeedProperty, value);
    }

    private static SolidColorBrush Muted => new(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));

    internal int Stop => PlaybackSpeeds.NearestStop(Speed);

    /// <summary>Picks the stop at <paramref name="stop"/>, as the viewer.</summary>
    internal void Pick(int stop)
    {
        stop = Math.Clamp(stop, 0, PlaybackSpeeds.Stops.Count - 1);
        double previous = Speed;
        double speed = PlaybackSpeeds.Stops[stop];
        if (Math.Abs(speed - previous) < 0.001)
        {
            return;
        }
        Speed = speed;
        SpeedPicked?.Invoke(this, speed);
        (FrameworkElementAutomationPeer.FromElement(this) as SpeedTrackAutomationPeer)?.RaiseValueChanged(previous, speed);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SpeedTrackAutomationPeer(this);

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        int? stop = e.Key switch
        {
            // Up and Down are the menu's: they move to the rows above and below.
            VirtualKey.Left => Stop - 1,
            VirtualKey.Right => Stop + 1,
            VirtualKey.Home => 0,
            VirtualKey.End => PlaybackSpeeds.Stops.Count - 1,
            _ => null,
        };
        if (stop is { } target)
        {
            e.Handled = true;
            Pick(target);
            return;
        }
        base.OnKeyDown(e);
    }

    private void OnTrackPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Track);
        if (!point.Properties.IsLeftButtonPressed || !Track.CapturePointer(e.Pointer))
        {
            return;
        }
        e.Handled = true;
        Focus(FocusState.Pointer);
        PickAt(point.Position.X);
    }

    private void OnTrackMoved(object sender, PointerRoutedEventArgs e)
    {
        if (Track.PointerCaptures?.Any(p => p.PointerId == e.Pointer.PointerId) == true)
        {
            PickAt(e.GetCurrentPoint(Track).Position.X);
        }
    }

    private void PickAt(double x)
    {
        double width = Track.ActualWidth;
        if (width > 0)
        {
            Pick((int)Math.Round(Math.Clamp(x / width, 0, 1) * (PlaybackSpeeds.Stops.Count - 1)));
        }
    }

    private double StopX(int stop) => Track.ActualWidth * stop / (PlaybackSpeeds.Stops.Count - 1);

    private void Render()
    {
        ValueText.Text = PlaybackSpeeds.Format(Speed);
        double width = Track.ActualWidth;
        if (width <= 0)
        {
            return;
        }
        int current = Stop;
        for (int stop = 0; stop < _ticks.Count; stop++)
        {
            Canvas.SetLeft(_ticks[stop], StopX(stop) - 0.5);
            Canvas.SetTop(_ticks[stop], TrackTop - _ticks[stop].Height / 2);
        }
        // From 1x to the speed: how far playback is from normal.
        double from = StopX(Math.Min(current, NormalStop));
        _fill.Width = Math.Abs(StopX(current) - StopX(NormalStop));
        Canvas.SetLeft(_fill, from);
        Canvas.SetTop(_fill, TrackTop - 1.5);
        Canvas.SetLeft(_thumb, StopX(current) - ThumbSize / 2);
        Canvas.SetTop(_thumb, TrackTop - ThumbSize / 2);
        foreach (var label in _labels)
        {
            int stop = PlaybackSpeeds.NearestStop((double)label.Tag);
            label.Foreground = stop == current ? new SolidColorBrush(Colors.White) : Muted;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double x = StopX(stop);
            // The ends' labels sit inside the track, the others centred under their stop.
            Canvas.SetLeft(label, stop == 0 ? x : stop == PlaybackSpeeds.Stops.Count - 1 ? x - label.DesiredSize.Width : x - label.DesiredSize.Width / 2);
        }
    }
}

/// <summary><see cref="SpeedTrack"/> as a slider for screen readers: its value is the speed, its text "1.5×".</summary>
internal sealed partial class SpeedTrackAutomationPeer(SpeedTrack owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider, IValueProvider
{
    private SpeedTrack Track => (SpeedTrack)Owner;

    public double Minimum => PlaybackSpeeds.Stops[0];

    public double Maximum => PlaybackSpeeds.Stops[^1];

    public double SmallChange => 0.25;

    public double LargeChange => 0.5;

    double IRangeValueProvider.Value => Track.Speed;

    string IValueProvider.Value => PlaybackSpeeds.Format(Track.Speed);

    bool IRangeValueProvider.IsReadOnly => false;

    bool IValueProvider.IsReadOnly => true;

    public void SetValue(double value) => Track.Pick(PlaybackSpeeds.NearestStop(value));

    public void SetValue(string value) => throw new InvalidOperationException("The speed's text is read-only.");

    public void RaiseValueChanged(double previous, double current)
    {
        if (ListenerExists(AutomationEvents.PropertyChanged))
        {
            RaisePropertyChangedEvent(Microsoft.UI.Xaml.Automation.RangeValuePatternIdentifiers.ValueProperty, previous, current);
        }
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

    protected override string GetClassNameCore() => nameof(SpeedTrack);

    protected override string GetNameCore() => base.GetNameCore() is { Length: > 0 } name ? name : "Playback speed";

    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface is PatternInterface.RangeValue or PatternInterface.Value ? this : base.GetPatternCore(patternInterface);
}
