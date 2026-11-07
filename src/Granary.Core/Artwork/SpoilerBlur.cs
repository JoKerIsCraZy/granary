namespace Granary.Core.Artwork;

/// <summary>How large a hidden episode still is decoded, and how much its pixels are blurred.</summary>
/// <param name="Sigma">The Gaussian's standard deviation, in the decode's pixels.</param>
public readonly record struct SpoilerBlurPlan(int Width, int Height, double Sigma);

/// <summary>
/// The blur of a hidden episode still (<see cref="Settings.EpisodeSpoilers"/>), the web's <c>blur-xl</c>: a Gaussian of
/// 24 px at the size the picture shows. The picture is decoded at an eighth of that width, where the blur is 3 px, and blurred
/// there: a few hundred pixels to work on, which the view then stretches over the card, smooth because nothing sharp is left.
/// </summary>
public static class SpoilerBlur
{
    /// <summary>Tailwind's <c>blur-xl</c>, in the view's pixels.</summary>
    public const double ShownSigma = 24;

    /// <summary>Fewer pixels across than this would show the stretch rather than a blur.</summary>
    public const int MinWidth = 8;

    // The decode's share of the width shown; the blur is then 3 of its pixels, which a stretch keeps smooth.
    private const double Step = 8;
    // Larger only takes longer: an eighth of a 1,024 px still.
    private const int MaxWidth = 128;
    // A card's width before it was laid out.
    private const double UnknownShownWidth = 320;

    /// <summary>The decode of a picture <paramref name="pixelWidth"/> × <paramref name="pixelHeight"/> shown <paramref name="shownWidth"/> wide.</summary>
    public static SpoilerBlurPlan Plan(double shownWidth, int pixelWidth, int pixelHeight)
    {
        double shown = double.IsFinite(shownWidth) && shownWidth > 0 ? shownWidth : UnknownShownWidth;
        int width = Math.Clamp((int)Math.Ceiling(shown / Step), MinWidth, MaxWidth);
        if (pixelWidth > 0)
        {
            width = Math.Max(1, Math.Min(width, pixelWidth));
        }
        int height = pixelWidth > 0 && pixelHeight > 0
            ? Math.Max(1, (int)Math.Round(width * (double)pixelHeight / pixelWidth, MidpointRounding.AwayFromZero))
            : Math.Max(1, (int)Math.Round(width * 9 / 16.0, MidpointRounding.AwayFromZero));
        return new SpoilerBlurPlan(width, height, ShownSigma * width / shown);
    }

    /// <summary>
    /// Blurs premultiplied BGRA pixels in place with a Gaussian of <paramref name="sigma"/> pixels, the edges extended so they
    /// keep their colour rather than fading out.
    /// </summary>
    public static void Blur(Span<byte> bgra, int width, int height, double sigma)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (bgra.Length < width * height * 4)
        {
            throw new ArgumentException("The pixels are fewer than the width and height say.", nameof(bgra));
        }
        if (!(sigma > 0))
        {
            return;
        }
        float[] kernel = Kernel(sigma);
        int radius = kernel.Length / 2;
        float[] across = new float[width * height * 4];
        // Along each row into the buffer, then down each column back into the pixels.
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int to = (y * width + x) * 4;
                for (int k = -radius; k <= radius; k++)
                {
                    int from = (y * width + Math.Clamp(x + k, 0, width - 1)) * 4;
                    float weight = kernel[k + radius];
                    for (int c = 0; c < 4; c++)
                    {
                        across[to + c] += bgra[from + c] * weight;
                    }
                }
            }
        }
        Span<float> sum = stackalloc float[4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                sum.Clear();
                for (int k = -radius; k <= radius; k++)
                {
                    int from = (Math.Clamp(y + k, 0, height - 1) * width + x) * 4;
                    float weight = kernel[k + radius];
                    for (int c = 0; c < 4; c++)
                    {
                        sum[c] += across[from + c] * weight;
                    }
                }
                int to = (y * width + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    bgra[to + c] = (byte)Math.Clamp(MathF.Round(sum[c]), 0, 255);
                }
            }
        }
    }

    // Three standard deviations each side, its weights summing to one.
    private static float[] Kernel(double sigma)
    {
        int radius = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        float[] kernel = new float[2 * radius + 1];
        double total = 0;
        for (int i = -radius; i <= radius; i++)
        {
            double weight = Math.Exp(-(i * i) / (2 * sigma * sigma));
            kernel[i + radius] = (float)weight;
            total += weight;
        }
        for (int i = 0; i < kernel.Length; i++)
        {
            kernel[i] = (float)(kernel[i] / total);
        }
        return kernel;
    }
}
