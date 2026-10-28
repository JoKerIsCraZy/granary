using System.Text.Json;
using Granary.Core.ViewModels.Cards;

namespace Granary.Tests.ViewModels;

/// <summary>The profile's poster size and caption, read as the web's parseCardPresentation, and the sizes they give cards.</summary>
public sealed class CardPresentationTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void AValidValue_IsRead()
    {
        var presentation = CardPresentation.Parse(Json("""{ "poster_size": "compact", "caption": "title" }"""));

        Assert.Equal(new CardPresentation(PosterSize.Compact, CardCaption.Title), presentation);
    }

    [Fact]
    public void EveryChoice_RoundTrips()
    {
        foreach (var size in Enum.GetValues<PosterSize>())
        {
            foreach (var caption in Enum.GetValues<CardCaption>())
            {
                var presentation = new CardPresentation(size, caption);

                Assert.Equal(presentation, CardPresentation.Parse(presentation.ToJson()));
            }
        }
    }

    [Fact]
    public void TheValueWritten_IsTheWebsDocument()
    {
        var json = new CardPresentation(PosterSize.Large, CardCaption.Artwork).ToJson();

        Assert.Equal("""{"poster_size":"large","caption":"artwork"}""", json.GetRawText());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"compact\"")]
    [InlineData("42")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "poster_size": "huge", "caption": "title" }""")]
    [InlineData("""{ "poster_size": "Compact", "caption": "title" }""")]
    [InlineData("""{ "poster_size": "compact", "caption": "everything" }""")]
    [InlineData("""{ "poster_size": "compact" }""")]
    [InlineData("""{ "caption": "title" }""")]
    [InlineData("""{ "poster_size": 1, "caption": "title" }""")]
    [InlineData("""{ "poster_size": "compact", "caption": null }""")]
    public void AnythingTheWebWouldNotRead_IsTheDefault(string value)
    {
        Assert.Same(CardPresentation.Default, CardPresentation.Parse(Json(value)));
    }

    [Fact]
    public void NoValue_IsTheDefault()
    {
        Assert.Same(CardPresentation.Default, CardPresentation.Parse(null));
        Assert.Equal(new CardPresentation(PosterSize.Standard, CardCaption.TitleMetadata), CardPresentation.Default);
    }

    [Fact]
    public void FieldsTheWebDoesNotKnow_AreIgnored()
    {
        var presentation = CardPresentation.Parse(Json("""{ "poster_size": "large", "caption": "title_metadata", "density": 3 }"""));

        Assert.Equal(new CardPresentation(PosterSize.Large, CardCaption.TitleMetadata), presentation);
    }

    [Theory]
    [InlineData(CardCaption.TitleMetadata, true, true)]
    [InlineData(CardCaption.Title, true, false)]
    [InlineData(CardCaption.Artwork, false, false)]
    public void TheCaption_SaysWhatShowsUnderAPoster(CardCaption caption, bool showsCaption, bool showsDetails)
    {
        var presentation = CardPresentation.Default with { Caption = caption };

        Assert.Equal((showsCaption, showsDetails), (presentation.ShowsCaption, presentation.ShowsDetails));
    }

    [Theory]
    [InlineData(0, WindowBreakpoint.Base)]
    [InlineData(639.9, WindowBreakpoint.Base)]
    [InlineData(640, WindowBreakpoint.Small)]
    [InlineData(767, WindowBreakpoint.Small)]
    [InlineData(768, WindowBreakpoint.Medium)]
    [InlineData(1023, WindowBreakpoint.Medium)]
    [InlineData(1024, WindowBreakpoint.Large)]
    [InlineData(1279, WindowBreakpoint.Large)]
    [InlineData(1280, WindowBreakpoint.ExtraLarge)]
    [InlineData(3840, WindowBreakpoint.ExtraLarge)]
    [InlineData(-5, WindowBreakpoint.Base)]
    [InlineData(double.NaN, WindowBreakpoint.Base)]
    [InlineData(double.PositiveInfinity, WindowBreakpoint.ExtraLarge)]
    public void TheWindowsWidth_FallsInTheWebsBreakpoints(double width, WindowBreakpoint breakpoint)
    {
        Assert.Equal(breakpoint, CardPresentation.BreakpointOf(width));
    }

    [Theory]
    [InlineData(PosterSize.Compact, new[] { 3, 5, 6, 8, 10 })]
    [InlineData(PosterSize.Standard, new[] { 3, 4, 5, 7, 8 })]
    [InlineData(PosterSize.Large, new[] { 2, 3, 4, 5, 6 })]
    public void AGrid_HasTheWebsColumns_AtEachBreakpoint(PosterSize size, int[] columns)
    {
        var presentation = CardPresentation.Default with { PosterSize = size };

        Assert.Equal(columns, Enum.GetValues<WindowBreakpoint>().Select(at => presentation.GridColumns(at)));
    }

    [Theory]
    [InlineData(PosterSize.Compact, 12)]
    [InlineData(PosterSize.Standard, 12)]
    [InlineData(PosterSize.Large, 16)]
    public void AGrid_HasTheWebsGap(PosterSize size, double gap)
    {
        Assert.Equal(gap, (CardPresentation.Default with { PosterSize = size }).GridGap);
    }

    [Theory]
    [InlineData(PosterSize.Compact, new[] { 120.0, 140, 140, 160, 160 })]
    [InlineData(PosterSize.Standard, new[] { 140.0, 160, 160, 185, 185 })]
    [InlineData(PosterSize.Large, new[] { 170.0, 195, 195, 220, 220 })]
    public void ARowsPosters_HaveTheWebsWidths(PosterSize size, double[] widths)
    {
        var presentation = CardPresentation.Default with { PosterSize = size };

        Assert.Equal(widths, Enum.GetValues<WindowBreakpoint>().Select(at => presentation.RowCardWidth(MediaCardShape.Poster, at)));
    }

    [Fact]
    public void ARowsWideCards_KeepTheWebsWidths_AtEveryPosterSize()
    {
        foreach (var size in Enum.GetValues<PosterSize>())
        {
            var presentation = CardPresentation.Default with { PosterSize = size };

            Assert.Equal([260.0, 315, 315, 315, 315],
                Enum.GetValues<WindowBreakpoint>().Select(at => presentation.RowCardWidth(MediaCardShape.Landscape, at)));
        }
    }

    [Theory]
    [InlineData(PosterSize.Compact, 3100, 19)]
    [InlineData(PosterSize.Standard, 3100, 16)]
    [InlineData(PosterSize.Large, 3100, 14)]
    [InlineData(PosterSize.Compact, 1900, 12)]
    public void AWideGrid_AddsColumns_SoItsPostersAreNoWiderThanARowsOnes(PosterSize size, double width, int columns)
    {
        var presentation = CardPresentation.Default with { PosterSize = size };

        int shown = presentation.GridColumns(WindowBreakpoint.ExtraLarge, width);

        Assert.Equal(columns, shown);
        double poster = (width - (shown - 1) * presentation.GridGap) / shown;
        Assert.True(poster <= presentation.RowCardWidth(MediaCardShape.Poster, WindowBreakpoint.ExtraLarge), $"{poster} px");
    }

    [Theory]
    [InlineData(PosterSize.Compact, WindowBreakpoint.ExtraLarge, 1000)]
    [InlineData(PosterSize.Standard, WindowBreakpoint.ExtraLarge, 1400)]
    [InlineData(PosterSize.Large, WindowBreakpoint.Large, 700)]
    [InlineData(PosterSize.Standard, WindowBreakpoint.Base, 360)]
    public void AGridWhosePostersFit_KeepsTheWebsColumns(PosterSize size, WindowBreakpoint at, double width)
    {
        var presentation = CardPresentation.Default with { PosterSize = size };

        Assert.Equal(presentation.GridColumns(at), presentation.GridColumns(at, width));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AGridOfNoKnownWidth_KeepsTheWebsColumns(double width)
    {
        Assert.Equal(8, CardPresentation.Default.GridColumns(WindowBreakpoint.ExtraLarge, width));
    }

    [Fact]
    public void ABreakpointOutOfRange_IsReadAsTheNearestOne()
    {
        Assert.Equal(8, CardPresentation.Default.GridColumns((WindowBreakpoint)99));
        Assert.Equal(3, CardPresentation.Default.GridColumns((WindowBreakpoint)(-1)));
    }
}
