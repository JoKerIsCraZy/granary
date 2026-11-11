namespace Granary.Core.ViewModels.Cards;

/// <summary>
/// Where a sideways row of equal cards scrolls to show one of them in the middle of its view, as the web's carousel
/// centres the episode a page is about; at the row's ends, as far as it scrolls.
/// </summary>
/// <param name="Offset">The horizontal offset to scroll to, within the scroll range the row has now.</param>
/// <param name="IsSettled">
/// The range is long enough for the card, or is the whole row's. False while the scroller has taken in only part of the
/// row: scrolled now, the row would stop short of the card.
/// </param>
public readonly record struct RowCentring(double Offset, bool IsSettled)
{
    // Layout rounding.
    private const double Tolerance = 1;

    /// <param name="index">The card's place in the row.</param>
    /// <param name="cards">How many cards the row holds.</param>
    /// <param name="startMargin">The space before the first card, inside the scrolled content.</param>
    /// <param name="endMargin">The space after the last card, inside the scrolled content.</param>
    /// <param name="scrollRange">How far the row scrolls now: its extent less its view.</param>
    public static RowCentring For(int index, int cards, double cardWidth, double spacing, double startMargin, double endMargin,
        double viewport, double scrollRange)
    {
        double target = startMargin + index * (cardWidth + spacing) + cardWidth / 2 - viewport / 2;
        double wholeRange = startMargin + cards * cardWidth + Math.Max(0, cards - 1) * spacing + endMargin - viewport;
        double range = Math.Max(0, scrollRange);
        bool settled = target <= 0 || range >= target || range >= wholeRange - Tolerance;
        return new RowCentring(Math.Clamp(target, 0, range), settled);
    }
}
