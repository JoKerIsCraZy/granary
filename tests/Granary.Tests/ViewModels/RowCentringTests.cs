using Granary.Core.ViewModels.Cards;

namespace Granary.Tests.ViewModels;

/// <summary>
/// Where a row of equal cards scrolls to show one as near its middle as whole cards allow, and when the scroller can take
/// it.
/// </summary>
public sealed class RowCentringTests
{
    // Twelve 300 px cards 20 apart, 12 px before the first and 48 after the last, in a 1200 px view: 3880 px of content.
    private const int Cards = 12;
    private const double Width = 300;
    private const double Spacing = 20;
    private const double Start = 12;
    private const double End = 48;
    private const double Viewport = 1200;
    private const double WholeRange = 3880 - Viewport;

    // A card and the space after it: the step the row's arrows page by.
    private const double Pitch = Width + Spacing;

    private static RowCentring Centre(int index, double scrollRange, double viewport = Viewport) =>
        RowCentring.For(index, Cards, Width, Spacing, Start, End, viewport, scrollRange);

    [Fact]
    public void ACardInTheRow_ComesAsNearTheMiddleAsAWholeCardAtTheStartAllows()
    {
        var centring = Centre(5, WholeRange);

        // Its middle at 12 + 5 × 320 + 150 = 1762 would put the view's start at 1162, in the middle of the fourth card.
        // The nearest place where a card starts the view, as after the row's arrows, is four cards in.
        Assert.Equal(new RowCentring(4 * Pitch, IsSettled: true), centring);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    public void TheRowOpens_WhereItsArrowsWouldLeaveIt_WithNoCardCutAtItsStart(int index)
    {
        double offset = Centre(index, WholeRange).Offset;

        // A whole number of cards scrolled, so the card at the start begins where the first one did.
        Assert.Equal(0, offset % Pitch, precision: 6);
        // And the card is in view, whole.
        double cardStart = Start + index * Pitch - offset;
        Assert.InRange(cardStart, 0, Viewport - Width);
    }

    [Fact]
    public void BetweenTwoPlaces_TheCardStandsBeforeTheMiddle_SoMoreOfWhatComesAfterItShows()
    {
        // A view that fits four cards has none in its middle: card 5's middle would put the view's start at
        // 12 + 1600 + 150 - 642 = 1120, exactly between three and four cards in.
        var centring = Centre(5, WholeRange, viewport: 1284);

        // Four cards in: the card is the second of the four shown, with two after it.
        Assert.Equal(new RowCentring(4 * Pitch, IsSettled: true), centring);
    }

    [Fact]
    public void TheFirstCards_StayAtTheStart()
    {
        Assert.Equal(new RowCentring(0, IsSettled: true), Centre(0, WholeRange));
        Assert.Equal(new RowCentring(0, IsSettled: true), Centre(1, scrollRange: 0));
    }

    [Fact]
    public void TheLastCard_ScrollsAsFarAsTheRowGoes()
    {
        Assert.Equal(new RowCentring(WholeRange, IsSettled: true), Centre(Cards - 1, WholeRange));
    }

    [Fact]
    public void AScrollRangeShortOfTheCard_WaitsForTheRestOfTheRow()
    {
        var centring = Centre(5, scrollRange: 400);

        Assert.False(centring.IsSettled);
        Assert.Equal(400, centring.Offset);
        Assert.False(Centre(5, scrollRange: 0).IsSettled);
        // Long enough for the card's middle, but not for the whole card the view is to start on.
        Assert.False(Centre(5, scrollRange: 1200).IsSettled);
    }

    [Fact]
    public void ARangeLongEnoughForTheCard_NeedNotBeTheWholeRow()
    {
        Assert.Equal(new RowCentring(4 * Pitch, IsSettled: true), Centre(5, scrollRange: 1500));
    }

    [Fact]
    public void ARangeWithinAPixelOfTheWholeRow_IsTheWholeRow()
    {
        Assert.True(Centre(Cards - 1, WholeRange - 0.5).IsSettled);
    }
}
