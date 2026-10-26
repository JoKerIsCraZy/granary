using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Controls;

/// <summary>
/// A sideways row that reaches the window's edge, as the web's carousels do: the scrolling track takes the page's gutter
/// on both sides as well, and its content keeps that gutter inside, so the first card still lines up with the page and the
/// last one ends a gutter from the edge, while scrolled cards run on to the edge instead of stopping at an invisible wall
/// 48 px before it. The arrows stay where the content starts and ends.
/// </summary>
internal static class RowBleed
{
    /// <summary>The page gutter the rows reach through (the pages' 48 px).</summary>
    public const double PageGutter = 48;

    // How far the arrows sit outside the content's start and end.
    private const double ArrowOverhang = 18;

    public static void Apply(double bleed, FrameworkElement track, FrameworkElement? content, Button previous, Button next)
    {
        track.Margin = new Thickness(-bleed, 0, -bleed, 0);
        if (content is not null)
        {
            content.Margin = new Thickness(bleed, 0, bleed, 0);
        }
        previous.Margin = new Thickness(bleed - ArrowOverhang, 0, 0, previous.Margin.Bottom);
        next.Margin = new Thickness(0, 0, bleed - ArrowOverhang, next.Margin.Bottom);
    }
}
