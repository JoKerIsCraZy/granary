using Granary.Core.Artwork;

namespace Granary.Tests.Artwork;

/// <summary>The blur of a hidden episode still: the web's blur-xl, worked out on a small decode of the picture.</summary>
public sealed class SpoilerBlurTests
{
    private static byte[] Pixels(int width, int height, Func<int, int, byte> value)
    {
        byte[] bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = (y * width + x) * 4;
                byte v = value(x, y);
                (bgra[at], bgra[at + 1], bgra[at + 2], bgra[at + 3]) = (v, v, v, 255);
            }
        }
        return bgra;
    }

    private static byte Blue(byte[] bgra, int width, int x, int y) => bgra[(y * width + x) * 4];

    [Fact]
    public void ThePicture_IsDecodedAnEighthAsWideAsItShows_KeepingItsShape()
    {
        var plan = SpoilerBlur.Plan(shownWidth: 320, pixelWidth: 1280, pixelHeight: 720);

        Assert.Equal((40, 23), (plan.Width, plan.Height));
        // blur-xl's 24 px at the size it shows, in the decode's pixels.
        Assert.Equal(3, plan.Sigma, 3);
    }

    [Fact]
    public void ASmallPicture_IsNotDecodedLargerThanItIs_AndItsBlurFollows()
    {
        var plan = SpoilerBlur.Plan(shownWidth: 800, pixelWidth: 50, pixelHeight: 25);

        Assert.Equal((50, 25), (plan.Width, plan.Height));
        Assert.Equal(1.5, plan.Sigma, 3);
    }

    [Fact]
    public void AnyPicture_KeepsAFewPixels()
    {
        var plan = SpoilerBlur.Plan(shownWidth: 0, pixelWidth: 400, pixelHeight: 225);

        Assert.True(plan.Width >= SpoilerBlur.MinWidth);
        Assert.True(plan.Height >= 1);
        Assert.True(plan.Sigma > 0);
    }

    [Fact]
    public void AnEvenPicture_StaysEven_AlsoAtItsEdges()
    {
        byte[] bgra = Pixels(12, 7, (_, _) => 90);

        SpoilerBlur.Blur(bgra, 12, 7, sigma: 3);

        Assert.All(bgra.Chunk(4), pixel => Assert.Equal([90, 90, 90, 255], pixel));
    }

    [Fact]
    public void ABrightSpot_SpreadsEvenlyAround_AndFades()
    {
        byte[] bgra = Pixels(21, 21, (x, y) => x == 10 && y == 10 ? (byte)255 : (byte)0);

        SpoilerBlur.Blur(bgra, 21, 21, sigma: 2);

        byte middle = Blue(bgra, 21, 10, 10);
        Assert.InRange(middle, 1, 254);
        Assert.Equal(Blue(bgra, 21, 8, 10), Blue(bgra, 21, 12, 10));
        Assert.Equal(Blue(bgra, 21, 10, 8), Blue(bgra, 21, 10, 12));
        Assert.True(Blue(bgra, 21, 12, 10) < middle);
        Assert.Equal(0, Blue(bgra, 21, 0, 0));
    }

    [Fact]
    public void AHardEdge_BecomesAGradient()
    {
        byte[] bgra = Pixels(16, 4, (x, _) => x < 8 ? (byte)0 : (byte)200);

        SpoilerBlur.Blur(bgra, 16, 4, sigma: 3);

        byte[] row = [.. Enumerable.Range(0, 16).Select(x => Blue(bgra, 16, x, 1))];
        Assert.Equal(row.Order(), row);
        Assert.InRange(row[7], 1, 199);
        Assert.InRange(row[8], 1, 199);
    }

    [Fact]
    public void NoBlur_LeavesThePicture()
    {
        byte[] bgra = Pixels(5, 5, (x, y) => (byte)(x * 40 + y));
        byte[] before = [.. bgra];

        SpoilerBlur.Blur(bgra, 5, 5, sigma: 0);

        Assert.Equal(before, bgra);
    }
}
