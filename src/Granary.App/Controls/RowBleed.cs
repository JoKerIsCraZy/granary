using Granary.Core.ViewModels.Cards;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Controls;

/// <summary>
/// A sideways row that reaches the window's right edge, as the web's carousels do: the scrolling track takes the page's
/// right gutter as well and keeps it inside its content, so the last card ends a gutter from the edge while the next one
/// shows in the gutter instead of stopping at an invisible wall 48 px before it. On the left the row ends at the page's
/// gutter, so no cut-off card shows beside the sidebar; the arrows page by whole cards, so one starts right there. The
/// arrows stay where the content starts and ends.
/// </summary>
internal static class RowBleed
{
    /// <summary>The page gutter the rows reach through (the pages' 48 px).</summary>
    public const double PageGutter = 48;

    // How far the arrows sit outside the content's start and end.
    private const double ArrowOverhang = 18;

    // How far the view reaches left of the first card and below the cards, for the focus ring that goes round a card
    // (SiloCardButtonStyle's FocusVisualMargin, 8 px, 12 below): the view, as high as the row's highest card, cut off the
    // first card's ring on its left and a three-line card's (Continue Watching, an episode) below. The content's margin
    // keeps the cards where they were; scrolled, this much of the card before shows left of the gutter.
    private const double FocusRoom = 12;
    private const double FocusRoomBelow = 14;

    /// <summary>
    /// Pages the row by whole cards, as the web's carousel does: as many as show, so that afterwards a card starts right on
    /// the page's gutter again, the one before it cut off in the gutter. A row whose cards cannot be measured pages by
    /// most of its width.
    /// </summary>
    public static void Page(ScrollView scroller, UIElement? content, double bleed, int direction)
    {
        double viewport = scroller.ViewportWidth - FocusRoom;
        double target;
        if (content is ItemsRepeater { Layout: StackLayout { Orientation: Orientation.Horizontal } layout } items && CardWidth(items) is { } width)
        {
            double pitch = width + layout.Spacing;
            int cards = Math.Max(1, (int)Math.Floor((viewport - bleed + layout.Spacing) / pitch));
            target = (Math.Round(scroller.HorizontalOffset / pitch) + direction * cards) * pitch;
        }
        else
        {
            target = scroller.HorizontalOffset + direction * viewport * 0.85;
        }
        scroller.ScrollTo(Math.Clamp(target, 0, scroller.ScrollableWidth), 0);
    }

    /// <summary>
    /// Scrolls the row at once so the card at <paramref name="index"/> is in the middle of its view, or as near as the row
    /// scrolls (<see cref="RowCentring"/>). False, without scrolling, while no card is laid out to measure or the scroller
    /// has taken in too little of the row to reach the card; on the <paramref name="lastTry"/> it scrolls as near as it
    /// can then.
    /// </summary>
    public static bool Centre(ScrollView scroller, ItemsRepeater items, int index, bool lastTry)
    {
        if (scroller.ViewportWidth <= 0 || items.Layout is not StackLayout { Orientation: Orientation.Horizontal } layout
            || items.ItemsSourceView is not { Count: > 0 } source || CardWidth(items) is not { } width)
        {
            return false;
        }
        var centring = RowCentring.For(index, source.Count, width, layout.Spacing, items.Margin.Left, items.Margin.Right,
            scroller.ViewportWidth, scroller.ScrollableWidth);
        if (!centring.IsSettled && !lastTry)
        {
            return false;
        }
        scroller.ScrollTo(centring.Offset, 0, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
        return true;
    }

    // A realized card's width: the row's cards are all one width.
    private static double? CardWidth(ItemsRepeater items)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(items); i++)
        {
            if (VisualTreeHelper.GetChild(items, i) is FrameworkElement { ActualWidth: > 0 } card)
            {
                return card.ActualWidth;
            }
        }
        return null;
    }

    public static void Apply(double bleed, FrameworkElement track, FrameworkElement scroller, FrameworkElement? content, Button previous, Button next)
    {
        track.Margin = new Thickness(0, 0, -bleed, 0);
        scroller.Margin = new Thickness(-FocusRoom, 0, 0, -FocusRoomBelow);
        if (content is not null)
        {
            content.Margin = new Thickness(FocusRoom, 0, bleed, FocusRoomBelow);
        }
        previous.Margin = new Thickness(-ArrowOverhang, 0, 0, previous.Margin.Bottom);
        next.Margin = new Thickness(0, 0, bleed - ArrowOverhang, next.Margin.Bottom);
    }
}
