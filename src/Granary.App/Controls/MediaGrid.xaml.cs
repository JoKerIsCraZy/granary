using System.Windows.Input;
using Granary.Core.ViewModels.Cards;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>
/// Cards in a grid that fills the width. <see cref="LoadMoreCommand"/> runs when a card near the end is
/// realized, so a paged list grows while the user scrolls.
/// </summary>
public sealed partial class MediaGrid : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(object), typeof(MediaGrid), new PropertyMetadata(null));

    public static readonly DependencyProperty CardShapeProperty =
        DependencyProperty.Register(nameof(CardShape), typeof(MediaCardShape), typeof(MediaGrid),
            new PropertyMetadata(MediaCardShape.Poster, OnCardShapeChanged));

    public static readonly DependencyProperty CardInvokedCommandProperty =
        DependencyProperty.Register(nameof(CardInvokedCommand), typeof(ICommand), typeof(MediaGrid), new PropertyMetadata(null));

    public static readonly DependencyProperty LoadMoreCommandProperty =
        DependencyProperty.Register(nameof(LoadMoreCommand), typeof(ICommand), typeof(MediaGrid), new PropertyMetadata(null));

    // About two screens of posters ahead of the end.
    private const int LoadMoreThreshold = 24;

    public MediaGrid()
    {
        InitializeComponent();
    }

    /// <summary>The cards: any list of <see cref="MediaCard"/>; an observable one grows in place.</summary>
    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>Posters (2:3, at least 185 px) or landscape stills (16:9, at least 280 px).</summary>
    public MediaCardShape CardShape
    {
        get => (MediaCardShape)GetValue(CardShapeProperty);
        set => SetValue(CardShapeProperty, value);
    }

    public ICommand? CardInvokedCommand
    {
        get => (ICommand?)GetValue(CardInvokedCommandProperty);
        set => SetValue(CardInvokedCommandProperty, value);
    }

    public ICommand? LoadMoreCommand
    {
        get => (ICommand?)GetValue(LoadMoreCommandProperty);
        set => SetValue(LoadMoreCommandProperty, value);
    }

    private static void OnCardShapeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((MediaGrid)sender).GridLayout.MinItemWidth = (MediaCardShape)e.NewValue == MediaCardShape.Landscape ? 280 : 185;

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        int count = sender.ItemsSourceView?.Count ?? 0;
        if (count > 0 && args.Index >= count - LoadMoreThreshold && LoadMoreCommand is { } command && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaCard card } && CardInvokedCommand is { } command && command.CanExecute(card))
        {
            command.Execute(card);
        }
    }
}
