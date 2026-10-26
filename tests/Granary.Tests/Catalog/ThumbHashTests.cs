using Granary.Core.Artwork;

namespace Granary.Tests.Catalog;

/// <summary>
/// The blurred picture of a thumbhash, against the reference implementation (the thumbhash package 0.1.1 the web uses):
/// the hashes are its encoding of two made-up images, the sizes and pixels its decoding of them.
/// </summary>
public sealed class ThumbHashTests
{
    [Theory]
    [InlineData("HwgKNRpwd4B3d3d4eHd3eIBwCPeI", 23, 32, 464263, "2,0,109,255", "128,130,130,255", "244,232,109,255")]
    [InlineData("XKeFG443QmdAeHdDQDj0i4+IeIh3iHc=", 32, 19, 277870, "25,37,126,241", "109,123,124,121", "88,156,120,0")]
    public void Decode_MatchesTheReference(string hash, int width, int height, long sum, string first, string middle, string last)
    {
        var picture = ThumbHash.FromBase64(hash);

        Assert.NotNull(picture);
        Assert.Equal((width, height), (picture.Width, picture.Height));
        Assert.Equal(sum, picture.Rgba.Sum(b => (long)b));
        Assert.Equal(first, Pixel(picture, 0, 0));
        Assert.Equal(middle, Pixel(picture, width / 2, height / 2));
        Assert.Equal(last, Pixel(picture, width - 1, height - 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("HwgK")]
    [InlineData("HwgKNRpwd4B3")]
    public void AHashThatDoesNotDecode_GivesNoPicture(string? hash)
    {
        Assert.Null(ThumbHash.FromBase64(hash));
    }

    [Fact]
    public void AnyHash_GivesAPictureOfAtMost32PixelsOrNone()
    {
        var random = new Random(26);
        for (int run = 0; run < 20000; run++)
        {
            var bytes = new byte[random.Next(0, 40)];
            random.NextBytes(bytes);

            var picture = ThumbHash.FromBase64(Convert.ToBase64String(bytes));

            if (picture is not null)
            {
                Assert.InRange(picture.Width, 1, 32);
                Assert.InRange(picture.Height, 1, 32);
                Assert.Equal(picture.Width * picture.Height * 4, picture.Rgba.Length);
            }
        }
    }

    [Fact]
    public void Bgra_IsPremultiplied()
    {
        var picture = new ThumbHash.Picture(2, 1, [200, 100, 50, 255, 200, 100, 50, 0]);

        var bgra = ThumbHash.ToPremultipliedBgra(picture);

        Assert.Equal([50, 100, 200, 255, 0, 0, 0, 0], bgra);
    }

    private static string Pixel(ThumbHash.Picture picture, int x, int y)
    {
        int i = (y * picture.Width + x) * 4;
        return string.Join(",", picture.Rgba[i..(i + 4)]);
    }
}
