namespace Granary.Core.Artwork;

/// <summary>
/// The cache identity of an artwork URL: the URL without its signature. Silo renews <c>exp</c> and <c>sig</c>
/// while the image stays the same (docs/api-contract.md, 7.10), and object storage does the same with its
/// <c>X-Amz-*</c> parameters. Any other query parameter may name a different image and stays in the key.
/// </summary>
public static class ArtworkCacheKey
{
    private static readonly HashSet<string> SignatureParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "exp", "sig", "Expires", "Signature", "Key-Pair-Id", "Policy",
    };

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
