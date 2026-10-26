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

    /// <summary>
    /// Pages the row by whole cards, as the web's carousel does: as many as show, so that afterwards a card starts right on
    /// the page's gutter again, the one before it cut off in the gutter. A row whose cards cannot be measured pages by
    /// most of its width.
    /// </summary>
    public static void Page(ScrollView scroller, UIElement? content, double bleed, int direction)
    {
        double viewport = scroller.ViewportWidth;
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

    public static void Apply(double bleed, FrameworkElement track, FrameworkElement? content, Button previous, Button next)
    {
        track.Margin = new Thickness(0, 0, -bleed, 0);
        if (content is not null)
        {
            content.Margin = new Thickness(0, 0, bleed, 0);
        }
        previous.Margin = new Thickness(-ArrowOverhang, 0, 0, previous.Margin.Bottom);
        next.Margin = new Thickness(0, 0, bleed - ArrowOverhang, next.Margin.Bottom);
    }
}
