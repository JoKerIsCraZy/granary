using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Images;

namespace Granary.Tests.Catalog;

public sealed class ImageSizesTests
{
    private static readonly ImageCapabilities Available =
        JsonSerializer.Deserialize(CatalogPayloads.ImageCapabilities, SiloJsonContext.Default.ImageCapabilities)!;

    [Fact]
    public void Default_SendsEverySlotTheServerAdvertises()
    {
        var parameters = ImageSizes.Default.For(Available);

        Assert.Equal("poster_width=342&backdrop_width=1280&logo_width=500&still_width=400&photo_width=185", parameters.ToString());
    }

    [Fact]
    public void For_KeepsOnlyAdvertisedSlots()
    {
        var postersOnly = Available with { SlotWidthParams = new Dictionary<string, string> { [ImageSlots.Poster] = "poster_width" } };

        var parameters = ImageSizes.Default.For(postersOnly);

        Assert.Equal([new KeyValuePair<string, string>("poster_width", "342")], parameters.Values);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("unsupported")]
    [InlineData("something_new")]
    public void For_SendsNothing_UnlessTheDocumentIsAvailable(string state)
    {
        Assert.Same(ImageParameters.None, ImageSizes.Default.For(Available with { State = state }));
        Assert.Same(ImageParameters.None, ImageSizes.Default.For(null));
    }

    [Fact]
    public void For_SendsASizeClassOnlyWhenListed()
    {
        var large = new ImageSizes(SizeClass: ImageSizeClasses.Large);

        Assert.Equal("image_size=large", large.For(Available).ToString());
        Assert.True(new ImageSizes(SizeClass: "huge").For(Available).IsEmpty);
        Assert.True(large.For(Available with { Sizes = ["small"] }).IsEmpty);
    }

    [Fact]
    public void For_CapsWidthsAndLeavesOutUnlaidOnes()
    {
        var parameters = new ImageSizes(Poster: 0, Backdrop: 10_000, Logo: -5, Still: 1).For(Available);

        Assert.Equal("backdrop_width=7680&still_width=1", parameters.ToString());
    }

    [Theory]
    // A hero 1920 px wide: 5 % narrower will do, as for the web.
    [InlineData(1920, 1824)]
    [InlineData(2560, 2432)]
    // Never past the widest size the server keeps: the original would come for every card of every row. Within 5 % of
    // it, it will do.
    [InlineData(2600, 2432)]
    [InlineData(3840, 2432)]
    [InlineData(100_000, 2432)]
    // Narrower than backdrops are asked for anyway, or not known: as they are.
    [InlineData(1100, 1280)]
    [InlineData(0, 1280)]
    [InlineData(double.NaN, 1280)]
    [InlineData(double.PositiveInfinity, 1280)]
    [InlineData(-1, 1280)]
    public void WithDrawnBackdrop_AsksForBackdropsAsWideAsAHeroDrawsThem(double devicePixels, int backdrop)
    {
        var sizes = ImageSizes.Default.WithDrawnBackdrop(devicePixels);

        Assert.Equal(ImageSizes.Default with { Backdrop = backdrop }, sizes);
    }

    [Theory]
    // 1280 px at a display scale of 150 % is wider than a hero of 1500 px needs: as it is.
    [InlineData(1.5, 1500, 1920)]
    // At 225 % and 300 % it is 2880 and 3840 px, past the widest size: a hero takes that size, not the original.
    [InlineData(2.25, 3000, 2560)]
    [InlineData(3, 3300, 2560)]
    public void WithDrawnBackdrop_KeepsBackdropsAskedForWiderAlready_UpToTheWidestSize(double scale, double devicePixels, int backdrop)
    {
        var scaled = ImageSizes.Default.ScaledBy(scale);

        Assert.Equal(scaled with { Backdrop = backdrop }, scaled.WithDrawnBackdrop(devicePixels));
    }

    [Theory]
    // The widest backdrop the server keeps is 2560 px: it stands in for the original while it covers four fifths.
    [InlineData(2560, "2560")]
    [InlineData(2561, "2560")]
    [InlineData(3200, "2560")]
    [InlineData(3201, "3201")]
    [InlineData(3648, "3648")]
    public void For_TakesTheWidestSizeRatherThanTheOriginal_WhileItNearlyCovers(int backdrop, string asked)
    {
        var parameters = new ImageSizes(Backdrop: backdrop).For(Available);

        Assert.Equal($"backdrop_width={asked}", parameters.ToString());
    }

    [Fact]
    public void For_KnowsAPhotosSizesAsProfile_AndLeavesAWidthWithoutListedSizesAlone()
    {
        Assert.Equal("photo_width=500", new ImageSizes(Photo: 600).For(Available).ToString());
        // A poster at a display scale of 300 %: the widest poster the server keeps is 1000 px.
        Assert.Equal(new KeyValuePair<string, string>("poster_width", "1000"), ImageSizes.Default.ScaledBy(3).For(Available).Values[0]);
        Assert.Equal("poster_width=1300", new ImageSizes(Poster: 1300).For(Available).ToString());
        Assert.Equal("poster_width=1100",
            new ImageSizes(Poster: 1100).For(Available with { Widths = new Dictionary<string, ImageSizeWidths>() }).ToString());
    }

    [Fact]
    public void ScaledBy_RoundsToWholePixelsAndKeepsUnsetSlots()
    {
        var scaled = new ImageSizes(Poster: 342, Logo: 500, SizeClass: ImageSizeClasses.Medium).ScaledBy(1.25);

        Assert.Equal(new ImageSizes(Poster: 428, Logo: 625, SizeClass: ImageSizeClasses.Medium), scaled);
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageSizes.Default.ScaledBy(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageSizes.Default.ScaledBy(double.NaN));
    }

    [Fact]
    public void Parameters_CompareByValue()
    {
        Assert.Equal(ImageSizes.Default.For(Available), ImageSizes.Default.For(Available));
        Assert.Equal(ImageSizes.Default.For(Available).GetHashCode(), ImageSizes.Default.For(Available).GetHashCode());
        Assert.NotEqual(ImageSizes.Default.For(Available), new ImageSizes(Poster: 342).For(Available));
    }
}
