namespace Granary.Core.Artwork;

/// <summary>
/// An artwork URL without the query parameters that only authorize it: Silo's <c>exp</c> and <c>sig</c>, a
/// Cloudflare token's <c>verify</c> and S3's <c>X-Amz-*</c> (docs/api-contract.md, 7.10). The server renews them
/// while the image stays the same. Any other parameter may choose the image and stays, sorted.
/// </summary>
/// <remarks>
/// The key names the same bytes across renewals only for an immutable image; one that can be replaced in
/// place keeps its full URL as its identity (<see cref="ArtworkLoader"/>).
/// </remarks>
public static class ArtworkCacheKey
{
    private static readonly HashSet<string> SignatureParameters = new(StringComparer.OrdinalIgnoreCase) { "exp", "sig", "verify" };

    public static string From(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri)
        {
            throw new ArgumentException("Artwork is cached by its absolute URL.", nameof(url));
        }
        string path = url.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
        var kept = url.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !IsSignature(pair))
            .Order(StringComparer.Ordinal)
            .ToList();
        return kept.Count == 0 ? path : path + "?" + string.Join('&', kept);
    }

    private static bool IsSignature(string pair)
    {
        int equals = pair.IndexOf('=');
        string name = equals < 0 ? pair : pair[..equals];
        return SignatureParameters.Contains(name) || name.StartsWith("X-Amz-", StringComparison.OrdinalIgnoreCase);
    }
}
