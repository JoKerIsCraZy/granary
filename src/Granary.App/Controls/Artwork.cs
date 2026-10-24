using Microsoft.UI.Xaml.Media.Imaging;

namespace Granary.App.Controls;

/// <summary>Loads catalog artwork.</summary>
internal static class Artwork
{
    /// <summary>
    /// Artwork URLs are presigned and public (docs/api-contract.md, 7.10): BitmapImage fetches them without
    /// credentials, as the server requires, and decodes only as many pixels as the card shows.
    /// </summary>
    public static BitmapImage? Load(Uri? url, int decodeWidth) =>
        url is null ? null : new BitmapImage(url) { DecodePixelWidth = decodeWidth, DecodePixelType = DecodePixelType.Logical };
}
