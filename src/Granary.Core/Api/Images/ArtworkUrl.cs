namespace Granary.Core.Api.Images;

/// <summary>
/// Makes the image members of v2 responses (<c>poster_url</c>, <c>backdrop_url</c>, <c>logo_url</c>,
/// <c>still_url</c>, <c>photo_url</c>, <c>avatar_url</c>, …) absolute (docs/api-contract.md, 7.10).
/// The URLs are presigned and short-lived: load them without credentials, and refetch the owning
/// card or detail when one past its <c>exp</c> answers 404.
/// </summary>
public static class ArtworkUrl
{
    /// <summary>
    /// Keeps an absolute http(s) URL, appends a root-relative one to <paramref name="serverBase"/>, and
    /// answers null for anything else (absent, empty, protocol-relative, other schemes).
    /// </summary>
    /// <remarks>
    /// The contract says to resolve root-relative values against the origin of the response, as the
    /// Android and Apple clients do. Granary appends them to the saved base URL instead, keeping a
    /// reverse-proxy path prefix: the server has no base-path setting and mints these URLs from a fixed
    /// route (<c>/api/v2/artwork/…</c>, silo-server <c>internal/artworkurl/artworkurl.go</c>), so they are
    /// relative to the server's own root, which for the client is the base URL every API call goes to.
    /// Without a prefix both give the same URL; behind <c>https://host/silo</c> the origin would point
    /// outside the server.
    /// </remarks>
    public static Uri? Resolve(Uri serverBase, string? value)
    {
        ArgumentNullException.ThrowIfNull(serverBase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (value[0] == '/')
        {
            // "//host/..." (and "/\host/...", which browsers read the same way) names another host.
            if (value.Length > 1 && value[1] is '/' or '\\')
            {
                return null;
            }
            return Uri.TryCreate(serverBase.AbsoluteUri.TrimEnd('/') + value, UriKind.Absolute, out var resolved) && IsHttp(resolved)
                ? resolved
                : null;
        }
        return Uri.TryCreate(value, UriKind.Absolute, out var absolute) && IsHttp(absolute) ? absolute : null;
    }

    private static bool IsHttp(Uri uri) => uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;
}
