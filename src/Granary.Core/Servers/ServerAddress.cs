namespace Granary.Core.Servers;

/// <summary>
/// Turns what the user typed into the server URLs to try, following the Android client
/// (docs/api-contract.md, 3.2): a bare host is tried as https, http and http:8090; host:port as
/// https and http; an explicit scheme only as given.
/// </summary>
public static class ServerAddress
{
    private const int FallbackPort = 8090;

    public static IReadOnlyList<Uri> Candidates(string? input)
    {
        string text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            return [];
        }

        int schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            string scheme = text[..schemeEnd].ToLowerInvariant();
            if (scheme is not ("http" or "https") || !TryBuild(text, out var explicitUri))
            {
                return [];
            }
            return [explicitUri];
        }

        string authority = AuthorityOf(text);
        string rest = text[authority.Length..];
        string hostPart = BracketBareIpv6(authority);
        bool hasPort = HasExplicitPort(hostPart);

        var candidates = new List<Uri>();
        foreach (string scheme in new[] { "https", "http" })
        {
            if (TryBuild($"{scheme}://{hostPart}{rest}", out var uri))
            {
                candidates.Add(uri);
            }
        }
        if (!hasPort && TryBuild($"http://{hostPart}:{FallbackPort}{rest}", out var fallback))
        {
            candidates.Add(fallback);
        }
        return candidates;
    }

    /// <summary>Lower-case scheme and host, no trailing slash; port, path and query are kept.</summary>
    public static Uri Normalize(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
            Fragment = "",
        };
        string path = builder.Path.TrimEnd('/');
        builder.Path = path;
        string normalized = builder.Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path | UriComponents.Query, UriFormat.UriEscaped);
        return new Uri(normalized.TrimEnd('/'));
    }

    private static bool TryBuild(string text, out Uri uri)
    {
        if (Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https"
            && parsed.Host.Length > 0)
        {
            uri = Normalize(parsed);
            return true;
        }
        uri = null!;
        return false;
    }

    private static string AuthorityOf(string text)
    {
        int end = text.IndexOfAny(['/', '?', '#']);
        return end < 0 ? text : text[..end];
    }

    // "fe80::1" has several colons and no brackets: an IPv6 literal without a port.
    private static string BracketBareIpv6(string authority) =>
        !authority.StartsWith('[') && authority.Count(c => c == ':') > 1 ? $"[{authority}]" : authority;

    private static bool HasExplicitPort(string authority) =>
        authority.StartsWith('[')
            ? authority.Contains("]:", StringComparison.Ordinal)
            : authority.Contains(':');
}
