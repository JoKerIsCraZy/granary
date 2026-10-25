using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Player;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Granary.App.Controls;

/// <summary>The marker panel over the player; it edits <see cref="PlayerViewModel.MarkerEditor"/>.</summary>
public sealed partial class MarkerEditPanel : UserControl
{
    // The card stays this far inside the player when it is moved.
    private const double EdgeMargin = 8;

    private PlayerViewModel? _viewModel;
    private Point? _dragFrom;
    private Point _dragOffset;

    public MarkerEditPanel()
    {
        InitializeComponent();
    }

    // Null until the page sets it: x:Bind skips what it cannot reach.
    public PlayerViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            Bindings.Update();
        }
    }

    /// <summary>Back where the web puts it, for the next opening.</summary>
    public void ResetPosition() => Offset.X = Offset.Y = 0;

    // Instance and static functions for x:Bind.
    public string Time(double seconds) => MarkerEditorViewModel.Time(seconds);

    public Visibility Shows(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility Hides(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    // The open row: white 6 % with a white 10 % ring; the others flat.
    public static Thickness RowPadding(bool isActive) => isActive ? new Thickness(10, 8, 10, 8) : new Thickness(10, 4, 10, 4);

    public static Brush RowBackground(bool isActive) => new SolidColorBrush(isActive ? Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : Colors.Transparent);

    public static Brush RowEdge(bool isActive) => new SolidColorBrush(isActive ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Colors.Transparent);

    public static double DotSize(bool isActive) => isActive ? 10 : 8;

    public static double DotOpacity(bool isActive) => isActive ? 1 : 0.5;

    public static double DotRing(bool isActive) => isActive ? 2 : 0;

    public static Brush LabelForeground(bool isActive) => new SolidColorBrush(isActive ? Colors.White : Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));

    public static Brush RangeForeground(bool hasRange) => new SolidColorBrush(Color.FromArgb(hasRange ? (byte)0xBF : (byte)0x59, 0xFF, 0xFF, 0xFF));

    public static string ResetName(string label) => $"Reset {label} marker";

    public static string ClearName(string label) => $"Clear {label} marker";

    private static MarkerEditRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as MarkerEditRow;

    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            row.Editor.SelectKind(row.Kind);
        }
    }

    private void OnSetStartClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            _viewModel?.SetMarkerEdgeHere(row.Kind, MarkerEdge.Start);
        }
    }

    private void OnSetEndClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            _viewModel?.SetMarkerEdgeHere(row.Kind, MarkerEdge.End);
        }
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            row.Editor.Reset(row.Kind);
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            row.Editor.Clear(row.Kind);
        }
    }

    // Moved by its header, as the web's, and kept inside the player.
    private void OnHeaderPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInButton(source))
        {
            return;
        }
        if (Header.CapturePointer(e.Pointer))
        {
            _dragFrom = e.GetCurrentPoint(Parent as UIElement).Position;
            _dragOffset = new Point(Offset.X, Offset.Y);
            e.Handled = true;
        }
    }

    private void OnHeaderMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragFrom is not { } from || Parent is not FrameworkElement host)
        {
            return;
        }
        var at = e.GetCurrentPoint(host).Position;
        // Where the card sits without the move, so the range stays the same however often it is moved.
        var layout = Card.TransformToVisual(host).TransformPoint(new Point(-Offset.X, -Offset.Y));
        double x = Math.Clamp(_dragOffset.X + at.X - from.X, EdgeMargin - layout.X,
            Math.Max(EdgeMargin - layout.X, host.ActualWidth - Card.ActualWidth - EdgeMargin - layout.X));
        double y = Math.Clamp(_dragOffset.Y + at.Y - from.Y, EdgeMargin - layout.Y,
            Math.Max(EdgeMargin - layout.Y, host.ActualHeight - Card.ActualHeight - EdgeMargin - layout.Y));
        Offset.X = x;
        Offset.Y = y;
        e.Handled = true;
    }

    private void OnHeaderReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragFrom is null)
        {
            return;
        }
        _dragFrom = null;
        Header.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private bool IsInButton(DependencyObject element)
    {
        for (var current = element; current is not null && current != Header; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>A grid that moves what it belongs to: the four-way cursor over it.</summary>
public sealed partial class MoveHandle : Grid
{
    public MoveHandle() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
}
