namespace Granary.Core.ViewModels.Cards;

/// <summary>
/// Where a sideways row of equal cards scrolls to show one of them, as the web's carousel brings the episode a page is
/// about into view: as near the middle of its view as it can stand with a whole card at the view's start, which is where
/// the row's arrows leave it too (a whole number of cards scrolled). Scrolled to the card's very middle, the card at the
/// start was cut in two, and its buttons with it. At the row's ends, as far as it scrolls.
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
        double pitch = cardWidth + spacing;
        double middle = startMargin + index * pitch + cardWidth / 2 - viewport / 2;
        // The nearest whole number of cards; right between two, the later, so the card stands before the middle and more
        // of what comes after it shows.
        double target = pitch > 0 ? Math.Floor(middle / pitch + 0.5) * pitch : middle;
        double wholeRange = startMargin + cards * cardWidth + Math.Max(0, cards - 1) * spacing + endMargin - viewport;
        double range = Math.Max(0, scrollRange);
        bool settled = target <= 0 || range >= target || range >= wholeRange - Tolerance;
        return new RowCentring(Math.Clamp(target, 0, range), settled);
    }
}
