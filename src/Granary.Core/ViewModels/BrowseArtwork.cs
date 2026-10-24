using Granary.Core.Api.Images;
using Granary.Core.Servers;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>The artwork sizes Home, "Explore all" and Search ask with (docs/api-contract.md, 7.10).</summary>
internal static class BrowseArtwork
{
    /// <summary>
    /// The default sizes at <paramref name="displayScale"/>, as the server understands them. When they
    /// cannot be read, the server's default sizes: artwork never stops a page from loading.
    /// </summary>
    public static async Task<ImageParameters> ParametersAsync(
        ImageParameterCache images, ServerEntry server, double displayScale, ILogger logger, CancellationToken cancellationToken)
    {
        double scale = double.IsFinite(displayScale) && displayScale > 0 ? displayScale : 1;
        ImageParameters parameters;
        try
        {
            parameters = await images.GetAsync(server, ImageSizes.Default.ScaledBy(scale), cancellationToken);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex, cancellationToken))
        {
            logger.LogWarning("Reading the image sizes failed: {Failure}", ApiFailureText.Describe(ex));
            parameters = ImageParameters.None;
        }
        // A newer load may have taken over while the sizes were read.
        cancellationToken.ThrowIfCancellationRequested();
        return parameters;
    }
}
