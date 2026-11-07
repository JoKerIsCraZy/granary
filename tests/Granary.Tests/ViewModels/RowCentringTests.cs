using Granary.Core.ViewModels.Cards;

namespace Granary.Tests.ViewModels;

/// <summary>Where a row of equal cards scrolls to show one in its middle, and when the scroller can take it.</summary>
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

    private static RowCentring Centre(int index, double scrollRange) =>
        RowCentring.For(index, Cards, Width, Spacing, Start, End, Viewport, scrollRange);

    [Fact]
    public void ACardInTheRow_ComesToTheMiddleOfTheView()
    {
        var centring = Centre(5, WholeRange);

        // Its middle at 12 + 5 × 320 + 150 = 1762, half the view before it.
        Assert.Equal(new RowCentring(1162, IsSettled: true), centring);
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
    }

    [Fact]
    public void ARangeLongEnoughForTheCard_NeedNotBeTheWholeRow()
    {
        Assert.Equal(new RowCentring(1162, IsSettled: true), Centre(5, scrollRange: 1500));
    }

    [Fact]
    public void ARangeWithinAPixelOfTheWholeRow_IsTheWholeRow()
    {
        Assert.True(Centre(Cards - 1, WholeRange - 0.5).IsSettled);
    }
}
