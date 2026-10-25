using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Granary.App.Controls;

/// <summary>A detail page row: title, a caption on the right ("3 total") and horizontally scrolling content with arrows for mouse users.</summary>
public sealed partial class DetailShelf : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(DetailShelf), new PropertyMetadata(""));

    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(nameof(Caption), typeof(string), typeof(DetailShelf), new PropertyMetadata(""));

    public static readonly DependencyProperty ShelfContentProperty =
        DependencyProperty.Register(nameof(ShelfContent), typeof(UIElement), typeof(DetailShelf), new PropertyMetadata(null));

    public static readonly DependencyProperty IsScrollerTabStopProperty =
        DependencyProperty.Register(nameof(IsScrollerTabStop), typeof(bool), typeof(DetailShelf), new PropertyMetadata(false));

    private bool _isPointerInside;

    public DetailShelf()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    /// <summary>The row's items, laid out horizontally.</summary>
    public UIElement? ShelfContent
    {
        get => (UIElement?)GetValue(ShelfContentProperty);
        set => SetValue(ShelfContentProperty, value);
    }

    /// <summary>Set when the content has nothing focusable, so keyboard users can still reach and scroll the row.</summary>
    public bool IsScrollerTabStop
    {
        get => (bool)GetValue(IsScrollerTabStopProperty);
        set => SetValue(IsScrollerTabStopProperty, value);
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e) => ScrollBy(-1);

    private void OnNextClick(object sender, RoutedEventArgs e) => ScrollBy(1);

    private void ScrollBy(int direction) =>
        Scroller.ScrollTo(Scroller.HorizontalOffset + direction * Scroller.ViewportWidth * 0.85, 0);

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerInside = e.Pointer.PointerDeviceType == PointerDeviceType.Mouse;
        UpdateArrows();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerInside = false;
        UpdateArrows();
    }

    private void OnViewChanged(ScrollView sender, object args) => UpdateArrows();

    private void UpdateArrows()
    {
        PreviousButton.Visibility = _isPointerInside && Scroller.HorizontalOffset > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _isPointerInside && Scroller.HorizontalOffset < Scroller.ScrollableWidth - 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
