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
    public void For_ClampsWidthsToTheServersRange()
    {
        var parameters = new ImageSizes(Poster: 0, Backdrop: 10_000).For(Available);

        Assert.Equal("poster_width=1&backdrop_width=7680", parameters.ToString());
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
