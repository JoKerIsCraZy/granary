using Granary.Core.Playback;
using Granary.Core.ViewModels.Player;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Granary.App.Controls;

// Editing markers, as Silo's web seek bar shows it: the parts brighten and grow past the track (the chosen one taller,
// with a white ring), and the chosen part has a white handle at each end. Dragging a handle moves that end, with the
// part and the time in the bubble above it.
public sealed partial class SeekBar
{
    public static readonly DependencyProperty EditKindProperty = DependencyProperty.Register(nameof(EditKind), typeof(string),
        typeof(SeekBar), new PropertyMetadata(null, (sender, _) => ((SeekBar)sender).OnMarkersChanged()));

    private const double HandleHitSize = 20;

    private readonly MarkerHandle _startHandle = new(MarkerEdge.Start);
    private readonly MarkerHandle _endHandle = new(MarkerEdge.End);
    private MarkerEdge? _edgeDrag;

    /// <summary>Raised while a marker handle is dragged, with the kind, the end and the time under the pointer.</summary>
    public event TypedEventHandler<SeekBar, MarkerEdgeChange>? MarkerEdgeDragged;

    /// <summary>The kind whose handles show while markers are edited; null when they are not.</summary>
    public string? EditKind
    {
        get => (string?)GetValue(EditKindProperty);
        set => SetValue(EditKindProperty, value);
    }

    private bool IsEditingMarkers => EditKind is not null;

    private void InitializeEditing()
    {
        foreach (var handle in new[] { _startHandle, _endHandle })
        {
            handle.PointerPressed += OnHandlePressed;
            handle.PointerMoved += OnHandleMoved;
            handle.PointerReleased += OnHandleReleased;
            handle.PointerCaptureLost += (_, _) => EndEdgeDrag();
            EditHandles.Children.Add(handle);
        }
    }

    // While editing, the parts are drawn outside the thin track, which clips them, brighter and taller.
    private void PlaceEditRegions(double width)
    {
        Regions.Visibility = IsEditingMarkers ? Visibility.Collapsed : Visibility.Visible;
        EditRegions.Children.Clear();
        _startHandle.Visibility = _endHandle.Visibility = Visibility.Collapsed;
        if (!IsEditingMarkers || Duration <= 0 || width <= 0)
        {
            return;
        }
        foreach (var marker in Markers ?? [])
        {
            bool active = marker.Kind == EditKind;
            double start = Math.Min(marker.StartSeconds, Duration) / Duration * width;
            double end = Math.Min(marker.EndSeconds, Duration) / Duration * width;
            double height = active ? 10 : 8;
            EditRegions.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Height = height,
                Width = Math.Max(height, end - start),
                Margin = new Thickness(start, 0, 0, 0),
                CornerRadius = new CornerRadius(height / 2),
                Background = new SolidColorBrush(EditTint(marker.Kind)),
                BorderBrush = active ? new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)) : null,
                BorderThickness = new Thickness(active ? 1 : 0),
            });
            if (active)
            {
                Place(_startHandle, start);
                Place(_endHandle, end);
            }
        }
    }

    private static void Place(MarkerHandle handle, double x)
    {
        handle.Visibility = Visibility.Visible;
        Canvas.SetLeft(handle, x - HandleHitSize / 2);
        Canvas.SetTop(handle, -HandleHitSize / 2);
    }

    // The web's editing tints: the part's colour at 70 %, credits at 75 %.
    private static Color EditTint(string kind)
    {
        var dot = Look(kind).Dot;
        return Color.FromArgb(kind == Core.Api.Playback.WatchMarkerKinds.Credits ? (byte)0xBF : (byte)0xB3, dot.R, dot.G, dot.B);
    }

    private void OnHandlePressed(object sender, PointerRoutedEventArgs e)
    {
        var handle = (MarkerHandle)sender;
        // The handle's own drag: the bar must not start a seek under it.
        e.Handled = true;
        if (EditKind is null || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed || !handle.CapturePointer(e.Pointer))
        {
            return;
        }
        _edgeDrag = handle.Edge;
        DragEdge(e);
    }

    private void OnHandleMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_edgeDrag is not null)
        {
            e.Handled = true;
            DragEdge(e);
        }
    }

    private void OnHandleReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_edgeDrag is null)
        {
            return;
        }
        e.Handled = true;
        DragEdge(e);
        ((MarkerHandle)sender).ReleasePointerCapture(e.Pointer);
        EndEdgeDrag();
    }

    private void DragEdge(PointerRoutedEventArgs e)
    {
        if (_edgeDrag is not { } edge || EditKind is not { } kind)
        {
            return;
        }
        double x = e.GetCurrentPoint(Track).Position.X;
        double seconds = SecondsAt(x);
        MarkerEdgeDragged?.Invoke(this, new MarkerEdgeChange(kind, edge, seconds));
        ShowEdgeTip(kind, edge);
    }

    private void EndEdgeDrag()
    {
        if (_edgeDrag is null)
        {
            return;
        }
        _edgeDrag = null;
        if (!_isPointerOver)
        {
            HideTip();
        }
    }

    // The bubble over the dragged end: "Intro start" and its time, where the end now is.
    private void ShowEdgeTip(string kind, MarkerEdge edge)
    {
        if ((Markers ?? []).FirstOrDefault(m => m.Kind == kind) is not { } marker || Duration <= 0)
        {
            return;
        }
        double seconds = edge == MarkerEdge.Start ? marker.StartSeconds : marker.EndSeconds;
        double x = Math.Min(seconds, Duration) / Duration * Track.ActualWidth;
        TipPicture.Visibility = Visibility.Collapsed;
        TipChapter.Visibility = Visibility.Collapsed;
        TipRegion.Visibility = Visibility.Visible;
        TipRegionDot.Fill = new SolidColorBrush(Look(kind).Dot);
        TipRegionName.Text = char.ToUpperInvariant(kind[0]) + kind[1..] + (edge == MarkerEdge.Start ? " start" : " end");
        TipRegionRange.Text = "";
        TipText.Text = MarkerEditorViewModel.Time(seconds);
        TipLines.HorizontalAlignment = HorizontalAlignment.Center;
        Tip.Width = double.NaN;
        ShowRegionHighlight(null);
        PlaceTip(x);
    }

    private bool IsHandle(object? source)
    {
        for (var element = source as DependencyObject; element is not null && element != this; element = VisualTreeHelper.GetParent(element))
        {
            if (element is MarkerHandle)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A handle: a 20 px target around the web's white 6 by 16 px grip, with the left-right cursor.</summary>
    private sealed partial class MarkerHandle : Grid
    {
        public MarkerHandle(MarkerEdge edge)
        {
            Edge = edge;
            Width = Height = HandleHitSize;
            Background = new SolidColorBrush(Colors.Transparent);
            Visibility = Visibility.Collapsed;
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
            Children.Add(new Border
            {
                Width = 6,
                Height = 16,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, edge == MarkerEdge.Start ? "Drag marker start" : "Drag marker end");
        }

        public MarkerEdge Edge { get; }
    }
}

/// <summary>A marker handle moved: which kind, which end, and the time it is at now.</summary>
public sealed record MarkerEdgeChange(string Kind, MarkerEdge Edge, double Seconds);
