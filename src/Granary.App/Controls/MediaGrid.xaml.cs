using System.Windows.Input;
using Granary.Core.ViewModels.Cards;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>
/// Cards in a grid that fills the width. <see cref="LoadMoreCommand"/> runs when a card near the end is
/// realized, so a paged list grows while the user scrolls. The cards' artwork is prefetched as they arrive.
/// </summary>
public sealed partial class MediaGrid : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(object), typeof(MediaGrid),
            new PropertyMetadata(null, (sender, _) => ((MediaGrid)sender).FollowItems()));

    public static readonly DependencyProperty CardShapeProperty =
        DependencyProperty.Register(nameof(CardShape), typeof(MediaCardShape), typeof(MediaGrid),
            new PropertyMetadata(MediaCardShape.Poster, OnCardShapeChanged));

    public static readonly DependencyProperty CardInvokedCommandProperty =
        DependencyProperty.Register(nameof(CardInvokedCommand), typeof(ICommand), typeof(MediaGrid), new PropertyMetadata(null));

    public static readonly DependencyProperty LoadMoreCommandProperty =
        DependencyProperty.Register(nameof(LoadMoreCommand), typeof(ICommand), typeof(MediaGrid), new PropertyMetadata(null));

    // The next page is asked for this many rows before the last realized card (which is about a screen below
    // the visible ones), so its artwork is prefetched before the user scrolls there.
    private const int LoadMoreRowsAhead = 4;

    private readonly CardArtworkPrefetch _prefetch = new();

    public MediaGrid()
    {
        InitializeComponent();
        Loaded += (_, _) => FollowItems();
        Unloaded += (_, _) => _prefetch.Follow(null);
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

    private void FollowItems()
    {
        if (IsLoaded)
        {
            _prefetch.Follow(ItemsSource);
        }
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        int count = sender.ItemsSourceView?.Count ?? 0;
        int columns = Math.Max(1, (int)((sender.ActualWidth + GridLayout.MinColumnSpacing) / (GridLayout.MinItemWidth + GridLayout.MinColumnSpacing)));
        if (count > 0 && args.Index >= count - columns * LoadMoreRowsAhead && LoadMoreCommand is { } command && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        // The card comes from the button's content: x:Bind templates in an ItemsRepeater set no DataContext.
        if (sender is Button { Content: MediaCardView { Card: { } card } } && CardInvokedCommand is { } command && command.CanExecute(card))
        {
            command.Execute(card);
        }
    }
}
