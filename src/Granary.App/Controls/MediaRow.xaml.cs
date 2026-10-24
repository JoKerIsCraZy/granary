using System.Windows.Input;
using Granary.Core.ViewModels.Cards;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Granary.App.Controls;

/// <summary>
/// A titled row of cards. <see cref="CardInvokedCommand"/> gets the <see cref="MediaCard"/>; "Explore all"
/// shows only when <see cref="ExploreAllCommand"/> is set.
/// </summary>
public sealed partial class MediaRow : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(MediaRow), new PropertyMetadata(""));

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(object), typeof(MediaRow), new PropertyMetadata(null));

    public static readonly DependencyProperty CardInvokedCommandProperty =
        DependencyProperty.Register(nameof(CardInvokedCommand), typeof(ICommand), typeof(MediaRow), new PropertyMetadata(null));

    public static readonly DependencyProperty ExploreAllCommandProperty =
        DependencyProperty.Register(nameof(ExploreAllCommand), typeof(ICommand), typeof(MediaRow), new PropertyMetadata(null));

    public const double PosterWidth = 185;
    public const double LandscapeWidth = 314;

    private bool _isPointerInside;

    public MediaRow()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The cards: any enumerable of <see cref="MediaCard"/>.</summary>
    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public ICommand? CardInvokedCommand
    {
        get => (ICommand?)GetValue(CardInvokedCommandProperty);
        set => SetValue(CardInvokedCommandProperty, value);
    }

    public ICommand? ExploreAllCommand
    {
        get => (ICommand?)GetValue(ExploreAllCommandProperty);
        set => SetValue(ExploreAllCommandProperty, value);
    }

    // Static and instance helpers because x:Bind function bindings call them.
    public static double CardWidth(MediaCardShape shape) => shape == MediaCardShape.Landscape ? LandscapeWidth : PosterWidth;

    public Visibility HasCommand(ICommand? command) => command is null ? Visibility.Collapsed : Visibility.Visible;

    public string ExploreAllName(string title) => $"Explore all: {title}";

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaCard card } && CardInvokedCommand is { } command && command.CanExecute(card))
        {
            command.Execute(card);
        }
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e) => ScrollBy(-1);

    private void OnNextClick(object sender, RoutedEventArgs e) => ScrollBy(1);

    private void ScrollBy(int direction) =>
        Scroller.ChangeView(Scroller.HorizontalOffset + direction * Scroller.ViewportWidth * 0.85, null, null);

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

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateArrows();

    private void UpdateArrows()
    {
        PreviousButton.Visibility = _isPointerInside && Scroller.HorizontalOffset > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _isPointerInside && Scroller.HorizontalOffset < Scroller.ScrollableWidth - 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
