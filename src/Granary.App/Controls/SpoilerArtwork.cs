using System.Runtime.InteropServices.WindowsRuntime;
using Granary.Core.Artwork;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Granary.App.Controls;

/// <summary>
/// A hidden episode still, blurred as the web's <c>blur-xl</c> (<see cref="SpoilerBlur"/>): decoded a few dozen pixels wide
/// and blurred off the UI thread, then stretched over its box, which keeps it smooth. Cheaper than the sharp picture, and
/// nothing else is made for it. The latest blurs are kept (<see cref="BlurredStills"/>), so a card shown again only makes its
/// bitmap. The sharp picture is never the fallback: one that does not decode leaves its blur's placeholder.
/// </summary>
internal static class SpoilerArtwork
{
    private static readonly BlurredStills Blurred = new();

    private static ILogger Logger => field ??= App.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SpoilerArtwork));

    /// <summary>The blurred picture of <paramref name="url"/> for a box <paramref name="shownWidth"/> wide; null when there is none.</summary>
    public static async Task<ImageSource?> LoadAsync(Uri url, double shownWidth, CancellationToken cancellationToken)
    {
        try
        {
            var pixels = Blurred.Find(url, shownWidth);
            if (pixels is null)
            {
                using var stream = await Artwork.StreamAsync(url, cancellationToken);
                if (stream is null || cancellationToken.IsCancellationRequested)
                {
                    return null;
                }
                pixels = await Task.Run(() => DecodeAsync(stream, shownWidth), CancellationToken.None);
                if (pixels is null)
                {
                    return null;
                }
                Blurred.Add(url, shownWidth, pixels);
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
            using (var target = bitmap.PixelBuffer.AsStream())
            {
                target.Write(pixels.Bgra);
            }
            bitmap.Invalidate();
            return bitmap;
        }
        catch (Exception ex)
        {
            // A blur is a nicety over its placeholder, never a reason to show the picture sharp.
            Logger.LogWarning("A hidden still {Path} could not be blurred: {Failure}", url.AbsolutePath, ex.GetType().Name);
            return null;
        }
    }

    // On the thread pool: the decoder is agile, and the blur works on a few thousand pixels.
    private static async Task<BlurredPicture?> DecodeAsync(IRandomAccessStream stream, double shownWidth)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var plan = SpoilerBlur.Plan(shownWidth, (int)decoder.PixelWidth, (int)decoder.PixelHeight);
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)plan.Width,
            ScaledHeight = (uint)plan.Height,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] bgra = data.DetachPixelData();
        if (bgra.Length < plan.Width * plan.Height * 4)
        {
            return null;
        }
        SpoilerBlur.Blur(bgra, plan.Width, plan.Height, plan.Sigma);
        return new BlurredPicture(plan.Width, plan.Height, bgra);
    }
}
