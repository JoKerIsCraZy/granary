using System.Windows.Input;
using Granary.Core.ViewModels.Cards;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Granary.App.Controls;

/// <summary>
/// A titled row of cards, as wide as the web's for the profile's poster size at the window's breakpoint.
/// <see cref="CardInvokedCommand"/> gets the <see cref="MediaCard"/>; "Explore all" shows only when
/// <see cref="ExploreAllCommand"/> is set. The cards' artwork is prefetched, so scrolling the row shows images at once.
/// </summary>
public sealed partial class MediaRow : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(MediaRow), new PropertyMetadata(""));

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(object), typeof(MediaRow),
            new PropertyMetadata(null, (sender, _) => ((MediaRow)sender).FollowItems()));

    public static readonly DependencyProperty CardInvokedCommandProperty =
        DependencyProperty.Register(nameof(CardInvokedCommand), typeof(ICommand), typeof(MediaRow), new PropertyMetadata(null));

    public static readonly DependencyProperty TitleStyleProperty =
        DependencyProperty.Register(nameof(TitleStyle), typeof(Style), typeof(MediaRow),
            new PropertyMetadata(null, (sender, e) => ((MediaRow)sender).TitleText.Style = e.NewValue as Style ?? (Style)Application.Current.Resources["SiloRowTitleTextStyle"]));

    public static readonly DependencyProperty ExploreAllCommandProperty =
        DependencyProperty.Register(nameof(ExploreAllCommand), typeof(ICommand), typeof(MediaRow), new PropertyMetadata(null));

    public static readonly DependencyProperty CardPointedCommandProperty =
        DependencyProperty.Register(nameof(CardPointedCommand), typeof(ICommand), typeof(MediaRow), new PropertyMetadata(null));

    public static readonly DependencyProperty CardRemoveCommandProperty =
        DependencyProperty.Register(nameof(CardRemoveCommand), typeof(ICommand), typeof(MediaRow), new PropertyMetadata(null));

    public static readonly DependencyProperty CardRemoveLabelProperty =
        DependencyProperty.Register(nameof(CardRemoveLabel), typeof(string), typeof(MediaRow), new PropertyMetadata(""));

    public static readonly DependencyProperty TitleBadgeProperty =
        DependencyProperty.Register(nameof(TitleBadge), typeof(string), typeof(MediaRow), new PropertyMetadata(""));

    /// <summary>How far the row reaches past its own edges to the window's (RowBleed); the page's gutter.</summary>
    public static readonly DependencyProperty BleedProperty =
        DependencyProperty.Register(nameof(Bleed), typeof(double), typeof(MediaRow), new PropertyMetadata(RowBleed.PageGutter, (sender, _) => ((MediaRow)sender).ApplyBleed()));

    private readonly CardArtworkPrefetch _prefetch = new();
    private readonly CardLook _look;
    private bool _isPointerInside;

    public MediaRow()
    {
        InitializeComponent();
        _look = new CardLook(this);
        ApplyBleed();
        Loaded += (_, _) =>
        {
            FollowItems();
            // The row's cards wait for it and for the page to stop before their artwork shows.
            ScrollMotion.Follow(Scroller);
            if (PageScroller() is { } page)
            {
                ScrollMotion.Follow(page);
            }
        };
        Unloaded += (_, _) => _prefetch.Follow(null);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>How far the row reaches past its own edges to the window's: the gutter of the page it is on.</summary>
    public double Bleed
    {
        get => (double)GetValue(BleedProperty);
        set => SetValue(BleedProperty, value);
    }

    /// <summary>The title's text style; the row title's (20 px semibold) when none is set.</summary>
    public Style? TitleStyle
    {
        get => (Style?)GetValue(TitleStyleProperty);
        set => SetValue(TitleStyleProperty, value);
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

    /// <summary>Gets the <see cref="MediaCard"/> the pointer or the keyboard came to (Home's hero follows it); null for none.</summary>
    public ICommand? CardPointedCommand
    {
        get => (ICommand?)GetValue(CardPointedCommandProperty);
        set => SetValue(CardPointedCommandProperty, value);
    }

    /// <summary>Takes a card (the parameter) off the row, for Continue Watching and Next Up; the cards' menus offer it.</summary>
    public ICommand? CardRemoveCommand
    {
        get => (ICommand?)GetValue(CardRemoveCommandProperty);
        set => SetValue(CardRemoveCommandProperty, value);
    }

    /// <summary>The menu entry for <see cref="CardRemoveCommand"/>: "Remove from Continue Watching".</summary>
    public string CardRemoveLabel
    {
        get => (string)GetValue(CardRemoveLabelProperty);
        set => SetValue(CardRemoveLabelProperty, value);
    }

    /// <summary>A red pill beside the title ("TODAY" on the calendar); empty for none.</summary>
    public string TitleBadge
    {
        get => (string)GetValue(TitleBadgeProperty);
        set => SetValue(TitleBadgeProperty, value);
    }

    // Instance helpers because x:Bind function bindings call them.
    public Visibility HasCommand(ICommand? command) => command is null ? Visibility.Collapsed : Visibility.Visible;

    public string ExploreAllName(string title) => $"Explore all: {title}";

    public Visibility HasText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    private void ApplyBleed() => RowBleed.Apply(Bleed, Track, Items, PreviousButton, NextButton);

    // The profile's choice and the window's breakpoint, taken before the cards are laid out in them.
    protected override Size MeasureOverride(Size availableSize)
    {
        if (_look.Update())
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(Items); i++)
            {
                if (VisualTreeHelper.GetChild(Items, i) is Button { Content: MediaCardView view } button)
                {
                    ShowLook(button, view, view.Card?.Shape ?? MediaCardShape.Poster);
                }
            }
        }
        return base.MeasureOverride(availableSize);
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is Button { Content: MediaCardView view } button)
        {
            var shape = sender.ItemsSourceView?.GetAt(args.Index) is MediaCard card ? card.Shape : MediaCardShape.Poster;
            ShowLook(button, view, shape);
        }
    }

    // The web's card width for the shape (carouselCardWidthPx), and the caption.
    private void ShowLook(Button button, MediaCardView view, MediaCardShape shape)
    {
        button.Width = _look.Presentation.RowCardWidth(shape, _look.Breakpoint);
        view.Caption = _look.Presentation.Caption;
    }

    private ScrollViewer? PageScroller()
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

    private void FollowItems()
    {
        if (IsLoaded)
        {
            _prefetch.Follow(ItemsSource);
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

    // Each card's own: a card's PointerEntered does not reach the row (it does not bubble), and a row without
    // CardPointedCommand does nothing more than the check.
    private void OnCardPointerEntered(object sender, PointerRoutedEventArgs e) => TellPointed(sender);

    private void OnCardGotFocus(object sender, RoutedEventArgs e) => TellPointed(sender);

    private void TellPointed(object sender)
    {
        if (CardPointedCommand is { } command && sender is Button { Content: MediaCardView { Card: { } card } } && command.CanExecute(card))
        {
            command.Execute(card);
        }
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e) => ScrollBy(-1);

    private void OnNextClick(object sender, RoutedEventArgs e) => ScrollBy(1);

    private void ScrollBy(int direction) => RowBleed.Page(Scroller, Items, Bleed, direction);

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
