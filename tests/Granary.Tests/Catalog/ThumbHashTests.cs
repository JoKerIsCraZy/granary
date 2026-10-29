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

    // Every byte of four pictures as the decoder gave them before it moved to flat arrays (98de803): with alpha, landscape,
    // portrait, and five pixels high. The hashes are seeded random bytes, so the pictures are loud, but each byte counts.
    [Theory]
    [InlineData("s1WjfDSPaRVX5WlUrhBqpM6hYSxgvYIIEbE=", 26, 32, "DE6D3574C2DFF03CB25AD1789361F386F247AC7B4CF81A1E2E87CB475B56F469", "127,0,255,0", "255,72,120,255", "255,255,255,145")]
    [InlineData("K11Is9inClG2XRVRNXpIts9ncHtskz8A7tw=", 32, 14, "B67ED072A7E9BFB9F83DC044C37ED238A669FC9AFD25977586B921CC0F495AB9", "0,0,0,255", "38,255,0,255", "153,0,0,255")]
    [InlineData("aQkJlTA1iJZiwK+y+y3Au4OSWlxIFCqgswE=", 23, 32, "1313E277C07CB682B80FC73C88C259562CE33FB8EBB203CE045D373A56B7C455", "47,255,255,255", "129,255,228,255", "255,255,0,255")]
    [InlineData("T1EIEaFDZljHbjgaAf865rmUzA==", 32, 5, "E44EE3F204DC0463724B369AEAB1B148C59C3E338093C362FE2B5CC19F20CDCB", "0,37,85,255", "0,207,52,255", "0,30,101,255")]
    public void Decode_GivesEveryByteItGaveBefore(string hash, int width, int height, string sha256, string first, string middle, string last)
    {
        var picture = ThumbHash.FromBase64(hash);

        Assert.NotNull(picture);
        Assert.Equal((width, height), (picture.Width, picture.Height));
        Assert.Equal(sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(picture.Rgba)));
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
    public void Decode_GivesEveryPixelOfTheReferencesOwnLoops()
    {
        var random = new Random(29);
        int compared = 0;
        for (int run = 0; run < 5000; run++)
        {
            var bytes = new byte[random.Next(5, 40)];
            random.NextBytes(bytes);

            var expected = Try(() => StraightPort(bytes));
            var actual = Try(() => ThumbHash.Decode(bytes));

            Assert.Equal(expected is null, actual is null);
            if (expected is not null && actual is not null)
            {
                Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
                Assert.Equal(expected.Rgba, actual.Rgba);
                compared++;
            }
        }
        Assert.True(compared > 1000, $"only {compared} hashes decoded");
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

    private static ThumbHash.Picture? Try(Func<ThumbHash.Picture> decode)
    {
        try
        {
            return decode();
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    // The reference's thumbHashToRGBA as written, loop for loop, which Decode works out faster: the same products in the
    // same order, so the same bytes.
    private static ThumbHash.Picture StraightPort(byte[] hash)
    {
        int header24 = hash[0] | (hash[1] << 8) | (hash[2] << 16);
        int header16 = hash[3] | (hash[4] << 8);
        double lDc = (header24 & 63) / 63.0;
        double pDc = ((header24 >> 6) & 63) / 31.5 - 1;
        double qDc = ((header24 >> 12) & 63) / 31.5 - 1;
        double lScale = ((header24 >> 18) & 31) / 31.0;
        bool hasAlpha = (header24 >> 23) != 0;
        double pScale = ((header16 >> 3) & 63) / 63.0;
        double qScale = ((header16 >> 9) & 63) / 63.0;
        bool isLandscape = (header16 >> 15) != 0;
        int lx = Math.Max(3, isLandscape ? hasAlpha ? 5 : 7 : header16 & 7);
        int ly = Math.Max(3, isLandscape ? header16 & 7 : hasAlpha ? 5 : 7);
        double aDc = hasAlpha ? (hash[5] & 15) / 15.0 : 1;
        double aScale = hasAlpha ? (hash[5] >> 4) / 15.0 : 0;
        int acStart = hasAlpha ? 6 : 5;
        int acIndex = 0;
        List<double> Channel(int nx, int ny, double scale)
        {
            var ac = new List<double>();
            for (int cy = 0; cy < ny; cy++)
            {
                for (int cx = cy != 0 ? 0 : 1; cx * ny < nx * (ny - cy); cx++, acIndex++)
                {
                    ac.Add((((hash[acStart + (acIndex >> 1)] >> ((acIndex & 1) << 2)) & 15) / 7.5 - 1) * scale);
                }
            }
            return ac;
        }
        var lAc = Channel(lx, ly, lScale);
        var pAc = Channel(3, 3, pScale * 1.25);
        var qAc = Channel(3, 3, qScale * 1.25);
        var aAc = hasAlpha ? Channel(5, 5, aScale) : [];
        int headerLx = (hash[4] & 0x80) != 0 ? (hash[2] & 0x80) != 0 ? 5 : 7 : hash[3] & 7;
        int headerLy = (hash[4] & 0x80) != 0 ? hash[3] & 7 : (hash[2] & 0x80) != 0 ? 5 : 7;
        double ratio = (double)headerLx / headerLy;
        int w = (int)Math.Floor((ratio > 1 ? 32 : 32 * ratio) + 0.5);
        int h = (int)Math.Floor((ratio > 1 ? 32 / ratio : 32) + 0.5);
        if (w is < 1 or > 32 || h is < 1 or > 32)
        {
            throw new ArgumentException("no size");
        }
        var rgba = new byte[w * h * 4];
        for (int y = 0, i = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++, i += 4)
            {
                double l = lDc, p = pDc, q = qDc, a = aDc;
                double Fx(int cx) => Math.Cos(Math.PI / w * (x + 0.5) * cx);
                double Fy(int cy) => Math.Cos(Math.PI / h * (y + 0.5) * cy);
                for (int cy = 0, j = 0; cy < ly; cy++)
                {
                    double fy2 = Fy(cy) * 2;
                    for (int cx = cy != 0 ? 0 : 1; cx * ly < lx * (ly - cy); cx++, j++)
                    {
                        l += lAc[j] * Fx(cx) * fy2;
                    }
                }
                for (int cy = 0, j = 0; cy < 3; cy++)
                {
                    double fy2 = Fy(cy) * 2;
                    for (int cx = cy != 0 ? 0 : 1; cx < 3 - cy; cx++, j++)
                    {
                        double f = Fx(cx) * fy2;
                        p += pAc[j] * f;
                        q += qAc[j] * f;
                    }
                }
                if (hasAlpha)
                {
                    for (int cy = 0, j = 0; cy < 5; cy++)
                    {
                        double fy2 = Fy(cy) * 2;
                        for (int cx = cy != 0 ? 0 : 1; cx < 5 - cy; cx++, j++)
                        {
                            a += aAc[j] * Fx(cx) * fy2;
                        }
                    }
                }
                double b = l - 2.0 / 3 * p;
                double r = (3 * l - b + q) / 2;
                double g = r - q;
                rgba[i] = (byte)Math.Max(0, 255 * Math.Min(1, r));
                rgba[i + 1] = (byte)Math.Max(0, 255 * Math.Min(1, g));
                rgba[i + 2] = (byte)Math.Max(0, 255 * Math.Min(1, b));
                rgba[i + 3] = (byte)Math.Max(0, 255 * Math.Min(1, a));
            }
        }
        return new ThumbHash.Picture(w, h, rgba);
    }
}
