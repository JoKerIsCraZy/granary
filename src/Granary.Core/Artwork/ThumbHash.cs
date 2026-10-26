namespace Granary.Core.Artwork;

/// <summary>
/// Decodes a ThumbHash (https://evanw.github.io/thumbhash/), the ~25 byte placeholder the server sends with artwork
/// (<c>poster_thumbhash</c>, <c>backdrop_thumbhash</c>, <c>still_thumbhash</c>), into a small blurred picture of it, 32 px on
/// its long edge, as the web (the thumbhash package) and the Android app (ThumbHash.kt) do. A port of the reference
/// <c>thumbHashToRGBA</c>, so the picture matches the web's to the byte.
/// </summary>
public static class ThumbHash
{
    /// <summary>The picture: <see cref="Rgba"/> holds its pixels row by row as red, green, blue and alpha, not premultiplied.</summary>
    public sealed record Picture(int Width, int Height, byte[] Rgba);

    /// <summary>The picture of a base64 hash, or null for none or one that does not decode.</summary>
    public static Picture? FromBase64(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }
        try
        {
            return Decode(Convert.FromBase64String(hash));
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The picture of the hash's bytes; a hash too short for what its header says throws IndexOutOfRangeException, one
    /// whose header names no picture size ArgumentException.
    /// </summary>
    public static Picture Decode(ReadOnlySpan<byte> hash)
    {
        // The constants.
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

        // The varying factors, saturation boosted by 1.25 against quantization.
        int acStart = hasAlpha ? 6 : 5;
        int acIndex = 0;
        double[] DecodeChannel(ReadOnlySpan<byte> bytes, int nx, int ny, double scale)
        {
            var ac = new List<double>();
            for (int cy = 0; cy < ny; cy++)
            {
                for (int cx = cy != 0 ? 0 : 1; cx * ny < nx * (ny - cy); cx++, acIndex++)
                {
                    int nibble = (bytes[acStart + (acIndex >> 1)] >> ((acIndex & 1) << 2)) & 15;
                    ac.Add((nibble / 7.5 - 1) * scale);
                }
            }
            return [.. ac];
        }
        double[] lAc = DecodeChannel(hash, lx, ly, lScale);
        double[] pAc = DecodeChannel(hash, 3, 3, pScale * 1.25);
        double[] qAc = DecodeChannel(hash, 3, 3, qScale * 1.25);
        double[] aAc = hasAlpha ? DecodeChannel(hash, 5, 5, aScale) : [];

        // The DCT back into RGB. The cosines depend on x or y alone, so each is worked out once.
        double ratio = ApproximateAspectRatio(hash);
        int w = Round(ratio > 1 ? 32 : 32 * ratio);
        int h = Round(ratio > 1 ? 32 / ratio : 32);
        // A header naming no width or height (a damaged hash) gives no picture rather than an empty or endless one.
        if (w is < 1 or > 32 || h is < 1 or > 32)
        {
            throw new ArgumentException("The hash names no picture size.", nameof(hash));
        }
        int nfx = Math.Max(lx, hasAlpha ? 5 : 3);
        int nfy = Math.Max(ly, hasAlpha ? 5 : 3);
        var fxs = new double[w, nfx];
        var fys = new double[h, nfy];
        for (int x = 0; x < w; x++)
        {
            for (int cx = 0; cx < nfx; cx++)
            {
                fxs[x, cx] = Math.Cos(Math.PI / w * (x + 0.5) * cx);
            }
        }
        for (int y = 0; y < h; y++)
        {
            for (int cy = 0; cy < nfy; cy++)
            {
                fys[y, cy] = Math.Cos(Math.PI / h * (y + 0.5) * cy);
            }
        }

        var rgba = new byte[w * h * 4];
        for (int y = 0, i = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++, i += 4)
            {
                double l = lDc, p = pDc, q = qDc, a = aDc;
                for (int cy = 0, j = 0; cy < ly; cy++)
                {
                    double fy2 = fys[y, cy] * 2;
                    for (int cx = cy != 0 ? 0 : 1; cx * ly < lx * (ly - cy); cx++, j++)
                    {
                        l += lAc[j] * fxs[x, cx] * fy2;
                    }
                }
                for (int cy = 0, j = 0; cy < 3; cy++)
                {
                    double fy2 = fys[y, cy] * 2;
                    for (int cx = cy != 0 ? 0 : 1; cx < 3 - cy; cx++, j++)
                    {
                        double f = fxs[x, cx] * fy2;
                        p += pAc[j] * f;
                        q += qAc[j] * f;
                    }
                }
                if (hasAlpha)
                {
                    for (int cy = 0, j = 0; cy < 5; cy++)
                    {
                        double fy2 = fys[y, cy] * 2;
                        for (int cx = cy != 0 ? 0 : 1; cx < 5 - cy; cx++, j++)
                        {
                            a += aAc[j] * fxs[x, cx] * fy2;
                        }
                    }
                }
                double b = l - 2.0 / 3 * p;
                double r = (3 * l - b + q) / 2;
                double g = r - q;
                rgba[i] = Channel(r);
                rgba[i + 1] = Channel(g);
                rgba[i + 2] = Channel(b);
                rgba[i + 3] = Channel(a);
            }
        }
        return new Picture(w, h, rgba);
    }

    /// <summary>The picture's pixels as premultiplied blue, green, red and alpha, the layout of a WinUI bitmap.</summary>
    public static byte[] ToPremultipliedBgra(Picture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        var source = picture.Rgba;
        var bgra = new byte[source.Length];
        for (int i = 0; i < source.Length; i += 4)
        {
            int alpha = source[i + 3];
            bgra[i] = (byte)(source[i + 2] * alpha / 255);
            bgra[i + 1] = (byte)(source[i + 1] * alpha / 255);
            bgra[i + 2] = (byte)(source[i] * alpha / 255);
            bgra[i + 3] = (byte)alpha;
        }
        return bgra;
    }

    // The reference's thumbHashToApproximateAspectRatio: from the header alone, without decode's floor of 3.
    private static double ApproximateAspectRatio(ReadOnlySpan<byte> hash)
    {
        int header = hash[3];
        bool hasAlpha = (hash[2] & 0x80) != 0;
        bool isLandscape = (hash[4] & 0x80) != 0;
        int lx = isLandscape ? hasAlpha ? 5 : 7 : header & 7;
        int ly = isLandscape ? header & 7 : hasAlpha ? 5 : 7;
        return (double)lx / ly;
    }

    // JavaScript's Math.round: halves go up.
    private static int Round(double value) => (int)Math.Floor(value + 0.5);

    // The reference writes into a Uint8Array, which drops the fraction.
    private static byte Channel(double value) => (byte)Math.Max(0, 255 * Math.Min(1, value));
}
