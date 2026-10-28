using System.Collections;
using System.Collections.Specialized;
using System.Windows.Input;
using Granary.Core.ViewModels.Cards;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Granary.App.Controls;

/// <summary>
/// Cards in a grid that fills the width. <see cref="LoadMoreCommand"/> runs when a card near the end is
/// realized, so a paged list grows while the user scrolls. A list loaded by window instead gets what is in view
/// through <see cref="ViewportCommand"/> (a <see cref="GridViewport"/>), and <see cref="ScrollToIndex"/> brings a
/// card's row to the top. The cards' artwork is prefetched as they arrive. Once an episode's poster is among them, every
/// card keeps the line of its episode title: the grid sizes all cards by its first one.
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

    public static readonly DependencyProperty ViewportCommandProperty =
        DependencyProperty.Register(nameof(ViewportCommand), typeof(ICommand), typeof(MediaGrid), new PropertyMetadata(null));

    public static readonly DependencyProperty SelectionProperty =
        DependencyProperty.Register(nameof(Selection), typeof(CardSelection), typeof(MediaGrid), new PropertyMetadata(null, OnSelectionChanged));

    // The next page is asked for this many rows before the last realized card (which is about a screen below
    // the visible ones), so its artwork is prefetched before the user scrolls there.
    private const int LoadMoreRowsAhead = 4;

    // What is in view is told at most this often while the grid scrolls, and once more when it stops.
    private static readonly TimeSpan ViewportInterval = TimeSpan.FromMilliseconds(50);

    // A poster is never narrower: a window too narrow for the web's columns beside the sidebar has one column less. The web's
    // own narrowest, compact posters at 1024 px, are about 75 px wide.
    private const double MinPosterWidth = 64;
    private const double MinLandscapeWidth = 280;

    private readonly CardArtworkPrefetch _prefetch = new();
    private readonly CardLook _look;
    private readonly DispatcherQueueTimer _viewportTimer;
    private ScrollViewer? _scroller;
    private Rect _effectiveViewport;
    private GridViewport? _toldViewport;
    private INotifyCollectionChanged? _followed;
    private bool _reservesEpisodeLine;

    public MediaGrid()
    {
        InitializeComponent();
        _look = new CardLook(this);
        _viewportTimer = DispatcherQueue.CreateTimer();
        _viewportTimer.Interval = ViewportInterval;
        _viewportTimer.IsRepeating = false;
        _viewportTimer.Tick += (_, _) => TellViewport();
        Loaded += (_, _) =>
        {
            FollowItems();
            _scroller = FindScroller();
            if (_scroller is not null)
            {
                ScrollMotion.Follow(_scroller);
            }
        };
        Unloaded += (_, _) =>
        {
            _prefetch.Follow(null);
            Unfollow();
            _viewportTimer.Stop();
        };
    }

    /// <summary>The cards: any list of <see cref="MediaCard"/>; an observable one grows in place.</summary>
    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>
    /// Posters (2:3, in the web's columns for the profile's poster size at the window's breakpoint) or landscape stills (16:9,
    /// at least 280 px).
    /// </summary>
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

    /// <summary>Entries of the list's own in its cards' ⋯ menu (a collection's Edit and Delete); null adds none.</summary>
    // Set from code, not XAML: the page's own list (the XAML compiler stumbles on an interface-typed property here).
    public ICardMenuSource? CardMenu { get; set; }

    /// <summary>
    /// The picks while the grid selects (History's Select): the cards show their pick marks, and a click picks a card
    /// instead of invoking it. Null for a grid that never selects.
    /// </summary>
    public CardSelection? Selection
    {
        get => (CardSelection?)GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    /// <summary>Gets a <see cref="GridViewport"/> whenever what the grid shows of its list changes.</summary>
    public ICommand? ViewportCommand
    {
        get => (ICommand?)GetValue(ViewportCommandProperty);
        set => SetValue(ViewportCommandProperty, value);
    }

    /// <summary>Scrolls the page so the row holding the card at <paramref name="index"/> is at the top, without waiting for it.</summary>
    public void ScrollToIndex(int index)
    {
        if (_scroller?.Content is not UIElement content)
        {
            return;
        }
        // The rows as the list is now: a page that grew or trimmed it may not have been laid out yet.
        Items.UpdateLayout();
        if (Measure() is not { } grid)
        {
            return;
        }
        int row = Math.Clamp(index, 0, grid.Count - 1) / grid.Columns;
        double top = Items.TransformToVisual(content).TransformPoint(default).Y;
        _scroller.ChangeView(null, top + row * grid.Pitch, null, disableAnimation: true);
    }

    private static void OnCardShapeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((MediaGrid)sender).ApplyLook();

    // The profile's choice and the window's breakpoint, taken before the cards are laid out in them.
    protected override Size MeasureOverride(Size availableSize)
    {
        if (_look.Update())
        {
            ApplyLook();
        }
        return base.MeasureOverride(availableSize);
    }

    // The web's columns and gap (cardGridClasses) for posters; a wide card keeps its least width. And the cards' captions.
    private void ApplyLook()
    {
        var presentation = _look.Presentation;
        GridLayout.MinColumnSpacing = presentation.GridGap;
        GridLayout.MinRowSpacing = presentation.GridGap;
        if (CardShape == MediaCardShape.Landscape)
        {
            GridLayout.MinItemWidth = MinLandscapeWidth;
            GridLayout.ClearValue(UniformGridLayout.MaximumRowsOrColumnsProperty);
        }
        else
        {
            GridLayout.MinItemWidth = MinPosterWidth;
            GridLayout.MaximumRowsOrColumns = presentation.GridColumns(_look.Breakpoint);
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(Items); i++)
        {
            ShowCaption(VisualTreeHelper.GetChild(Items, i));
        }
    }

    private void ShowCaption(DependencyObject element)
    {
        switch (element)
        {
            case Button { Content: MediaCardView card }:
                card.Caption = _look.Presentation.Caption;
                break;
            case MediaCardSkeleton skeleton:
                skeleton.ShowsTitle = _look.Presentation.ShowsCaption;
                break;
        }
    }

    private void FollowItems()
    {
        if (IsLoaded)
        {
            _prefetch.Follow(ItemsSource);
            Follow(ItemsSource);
        }
    }

    private void Follow(object? items)
    {
        Unfollow();
        _followed = items as INotifyCollectionChanged;
        if (_followed is not null)
        {
            _followed.CollectionChanged += OnItemsChanged;
        }
        ReserveEpisodeLine(HasEpisodeTitle(items as IEnumerable));
    }

    // The kept line stays while the page is away: a cached page coming back keeps its rows, and Back its place.
    private void Unfollow()
    {
        if (_followed is not null)
        {
            _followed.CollectionChanged -= OnItemsChanged;
            _followed = null;
        }
    }

    // Only the cards that came need a look: a line once kept stays until the grid gets another list.
    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ReserveEpisodeLine(HasEpisodeTitle(sender as IEnumerable));
        }
        else if (!_reservesEpisodeLine && HasEpisodeTitle(e.NewItems))
        {
            ReserveEpisodeLine(true);
        }
    }

    private static bool HasEpisodeTitle(IEnumerable? cards) =>
        cards?.OfType<MediaCard>().Any(card => !string.IsNullOrEmpty(card.EpisodeTitle)) == true;

    private void ReserveEpisodeLine(bool reserve)
    {
        if (reserve == _reservesEpisodeLine)
        {
            return;
        }
        _reservesEpisodeLine = reserve;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(Items); i++)
        {
            if (VisualTreeHelper.GetChild(Items, i) is Button { Content: MediaCardView card })
            {
                card.ReservesEpisodeLine = reserve;
            }
        }
        Items.InvalidateMeasure();
    }

    private ScrollViewer? FindScroller()
    {
        for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer scroller)
            {
                return scroller;
            }
        }
        return null;
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        ShowCaption(args.Element);
        if (args.Element is Button { Content: MediaCardView card })
        {
            card.ReservesEpisodeLine = _reservesEpisodeLine;
            ShowPick(card);
        }
        int count = sender.ItemsSourceView?.Count ?? 0;
        if (count > 0 && args.Index >= count - Columns() * LoadMoreRowsAhead && LoadMoreCommand is { } command && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        _effectiveViewport = args.EffectiveViewport;
        QueueViewport();
    }

    // A new list or another window width moves the rows under the same viewport.
    private void OnItemsSizeChanged(object sender, SizeChangedEventArgs e) => QueueViewport();

    private void QueueViewport()
    {
        if (ViewportCommand is not null && !_viewportTimer.IsRunning)
        {
            _viewportTimer.Start();
        }
    }

    private void TellViewport()
    {
        if (ViewportCommand is not { } command || Measure() is not { } grid || _effectiveViewport.Height <= 0)
        {
            return;
        }
        int lastRow = (grid.Count - 1) / grid.Columns;
        int RowAt(double y) => Math.Clamp((int)Math.Floor(y / grid.Pitch), 0, lastRow);
        int firstRow = RowAt(_effectiveViewport.Top);
        int bottomRow = RowAt(_effectiveViewport.Bottom - 1);
        // The top row is the first at least half in view.
        int topRow = Math.Clamp((int)Math.Round(Math.Max(0, _effectiveViewport.Top) / grid.Pitch, MidpointRounding.AwayFromZero), 0, lastRow);
        bool atEnd = _scroller is { } scroller && scroller.VerticalOffset >= scroller.ScrollableHeight - 2;
        int LastOf(int row) => Math.Min(grid.Count - 1, (row + 1) * grid.Columns - 1);
        var viewport = new GridViewport(firstRow * grid.Columns, LastOf(bottomRow), LastOf(topRow), atEnd);
        if (viewport != _toldViewport && command.CanExecute(viewport))
        {
            _toldViewport = viewport;
            command.Execute(viewport);
        }
    }

    // As many as fit, up to the poster size's columns.
    private int Columns()
    {
        int fit = Math.Max(1, (int)((Items.ActualWidth + GridLayout.MinColumnSpacing) / (GridLayout.MinItemWidth + GridLayout.MinColumnSpacing)));
        int most = GridLayout.MaximumRowsOrColumns;
        return most > 0 ? Math.Min(most, fit) : fit;
    }

    // Every row of a uniform grid is as tall as the others, so the grid's height gives the pitch of its rows.
    private (int Count, int Columns, double Pitch)? Measure()
    {
        int count = Items.ItemsSourceView?.Count ?? 0;
        if (count == 0 || Items.ActualHeight <= 0)
        {
            return null;
        }
        int columns = Columns();
        int rows = (count + columns - 1) / columns;
        return (count, columns, (Items.ActualHeight + GridLayout.MinRowSpacing) / rows);
    }

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        // The card comes from the button's content: x:Bind templates in an ItemsRepeater set no DataContext.
        if (sender is not Button { Content: MediaCardView { Card: { } card } })
        {
            return;
        }
        if (Selection is { IsActive: true } selection)
        {
            selection.Toggle(card);
            return;
        }
        if (CardInvokedCommand is { } command && command.CanExecute(card))
        {
            command.Execute(card);
        }
    }

    private static void OnSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var grid = (MediaGrid)d;
        if (e.OldValue is CardSelection old)
        {
            old.Changed -= grid.OnPicksChanged;
        }
        if (e.NewValue is CardSelection selection)
        {
            selection.Changed += grid.OnPicksChanged;
        }
        grid.ShowPicks();
    }

    private void OnPicksChanged(object? sender, EventArgs e) => ShowPicks();

    // The realized cards' marks; a card realized later takes its own (OnElementPrepared).
    private void ShowPicks()
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(Items); i++)
        {
            if (VisualTreeHelper.GetChild(Items, i) is Button { Content: MediaCardView card })
            {
                ShowPick(card);
            }
        }
    }

    private void ShowPick(MediaCardView card) =>
        card.Pick = Selection is { IsActive: true } selection && card.Card is { IsPlaceholder: false } shown ? selection.Contains(shown.Id) : null;
}
