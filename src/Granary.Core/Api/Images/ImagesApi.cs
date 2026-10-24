namespace Granary.Core.Api.Images;

/// <summary>Artwork sizing (docs/api-contract.md, 7.10).</summary>
public sealed class ImagesApi(SiloHttp http)
{
    /// <summary>
    /// The sizes and per-slot width parameters this server accepts (profile optional). Refetch after
    /// sign-in, profile changes and app resume.
    /// </summary>
    public Task<ImageCapabilities> GetCapabilitiesAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/images/capabilities", SiloAuth.Profile, SiloJsonContext.Default.ImageCapabilities, cancellationToken);
}
