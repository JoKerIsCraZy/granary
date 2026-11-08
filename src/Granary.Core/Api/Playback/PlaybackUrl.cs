using System.Net;
using Granary.Core.Api.Images;

namespace Granary.Core.Api.Playback;

/// <summary>
/// Makes a plan's media, subtitle and font URLs absolute (docs/api-contract.md, 9.2). These requests carry the
/// bearer token, so only URLs on the saved server are accepted.
/// </summary>
public static class PlaybackUrl
{
    /// <summary>
    /// Appends a root-relative URL to <paramref name="server"/> as a string, keeping a reverse-proxy path prefix and the
    /// query exactly as sent (use <see cref="Uri.OriginalString"/>), or keeps an absolute one beneath the server. Answers
    /// null for anything else: without <c>authorized_media_origins_v1</c> no plan names another origin, and one that
    /// did must not receive the client's credentials.
    /// </summary>
    public static Uri? Resolve(Uri server, string? value)
    {
        var resolved = ArtworkUrl.Resolve(server, value);
        return resolved is not null && SiloRequestOptions.IsBeneath(resolved, server) ? resolved : null;
    }

    /// <summary>
    /// A plan's stream URL: on the saved server as <see cref="Resolve"/> takes it, or, once the attempt agreed on
    /// <c>authorized_media_origins_v1</c>, on a proxy node as the server builds those (Apple's client checks the
    /// same): HTTPS unless the server itself is plain HTTP, no user info, the session's own
    /// <c>/stream/v3/{session}</c> or its <c>master.m3u8</c>, and no query but <c>seek</c>. The proxy node checks the
    /// bearer itself, so it receives the same headers; never a loopback or link-local one.
    /// </summary>
    /// <remarks>
    /// The media proxy sends a URL as written, so what is checked must be what is written: no dot segments, nothing
    /// encoded that would become one, no backslash or blank. A node URL is taken in its canonical form.
    /// </remarks>
    public static Uri? ResolveStream(Uri server, string? value, string sessionId, bool mediaOrigins)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (Resolve(server, value) is { } onServer)
        {
            return IsPlain(onServer.OriginalString) ? onServer : null;
        }
        if (!mediaOrigins || !IsElsewhere(server, value) || !IsPlain(value!) || !Uri.TryCreate(value, UriKind.Absolute, out var node))
        {
            return null;
        }
        bool secure = node.Scheme == Uri.UriSchemeHttps || (node.Scheme == Uri.UriSchemeHttp && server.Scheme == Uri.UriSchemeHttp);
        string stream = "/stream/v3/" + Uri.EscapeDataString(sessionId);
        bool ownStream = node.AbsolutePath == stream || node.AbsolutePath == stream + "/master.m3u8";
        bool plainQuery = node.Query.Length == 0 || IsSeekOnly(node.Query);
        bool valid = secure && node.UserInfo.Length == 0 && ownStream && plainQuery && node.Fragment.Length == 0 && IsRemote(node);
        return valid ? new Uri(node.AbsoluteUri) : null;
    }

    /// <summary>
    /// A subtitle sidecar (an inventory entry's or the plan's artifact <c>url</c>, 9.6): on the saved server as
    /// <see cref="Resolve"/> takes it, and written as it will be sent, since the media proxy sends it unchanged.
    /// </summary>
    public static Uri? ResolveSidecar(Uri server, string? value)
    {
        ArgumentNullException.ThrowIfNull(server);
        return Resolve(server, value) is { } sidecar && IsPlain(sidecar.OriginalString) ? sidecar : null;
    }

    /// <summary>
    /// A theme grant's audio URL (docs/api-contract.md 7.19): on the saved server as <see cref="Resolve"/> takes it, or, while
    /// <paramref name="origins"/> lets one, on a proxy node's origin, decided as a video stream's is
    /// (<c>authorized_media_origins_v1</c>, <see cref="ResolveStream"/>): the server's own scheme, no user info, no fragment, and
    /// never this machine, a link-local, multicast or unspecified address, also written as an IPv4-mapped IPv6 one. A server
    /// that authorizes media origins vouches for the node, a private address included; without that, a private or
    /// unique-local node is taken only from a server that is on a private network itself. It carries its own token, so it is
    /// fetched without credentials.
    /// </summary>
    public static Uri? ResolveTheme(Uri server, string? value, ThemeOrigins origins)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (Resolve(server, value) is { } onServer)
        {
            return IsPlain(onServer.OriginalString) ? onServer : null;
        }
        if (origins == ThemeOrigins.Server || !IsElsewhere(server, value) || !IsPlain(value!)
            || !Uri.TryCreate(value, UriKind.Absolute, out var node))
        {
            return null;
        }
        if (node.Scheme != server.Scheme || node.UserInfo.Length > 0 || node.Fragment.Length > 0 || !IsRemote(node) || IsThisPC(node))
        {
            return null;
        }
        bool vouched = origins == ThemeOrigins.Authorized || !IsPrivate(node) || IsPrivate(server);
        return vouched ? new Uri(node.AbsoluteUri) : null;
    }

    /// <summary>An absolute HTTP URL that is not on the saved server: a proxy node's, or one the client refuses.</summary>
    public static bool IsElsewhere(Uri server, string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var absolute)
        && (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp)
        && !SiloRequestOptions.IsBeneath(absolute, server);

    // Written as it will be sent: nothing a server would read as another path than the one checked.
    private static bool IsPlain(string url)
    {
        if (url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\'))
        {
            return false;
        }
        string path = url.Split('?', '#')[0];
        int authority = path.IndexOf("//", StringComparison.Ordinal);
        if (authority >= 0)
        {
            int start = path.IndexOf('/', authority + 2);
            path = start < 0 ? "/" : path[start..];
        }
        return !path.Contains("%2e", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("%5c", StringComparison.OrdinalIgnoreCase)
            && path.Split('/').All(segment => segment is not ("." or ".."));
    }

    // A node is somewhere else on the network: not this machine, not a link-local or cloud metadata address.
    private static bool IsRemote(Uri node)
    {
        if (node.IsLoopback || node.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!IPAddress.TryParse(node.Host.Trim('[', ']'), out var address))
        {
            return true;
        }
        byte[] bytes = address.GetAddressBytes();
        bool ipv4LinkLocal = bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
        bool unspecified = address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
        bool multicast = address.IsIPv6Multicast || (bytes.Length == 4 && bytes[0] is >= 224 and <= 239);
        return !ipv4LinkLocal && !address.IsIPv6LinkLocal && !unspecified && !multicast;
    }

    // Never a theme's node, whoever names it: this machine, a link-local, multicast or unspecified address, also written as an
    // IPv4-mapped IPv6 address, which IsRemote reads as IPv6.
    private static bool IsThisPC(Uri node)
    {
        if (Literal(node) is not { } address)
        {
            return false;
        }
        byte[] bytes = address.GetAddressBytes();
        bool unspecified = address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || (bytes.Length == 4 && bytes[0] == 0);
        bool linkLocal = (bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254) || address.IsIPv6LinkLocal;
        bool multicast = (bytes.Length == 4 && bytes[0] >= 224) || address.IsIPv6Multicast;
        return IPAddress.IsLoopback(address) || unspecified || linkLocal || multicast;
    }

    // On a private network: a private (RFC 1918), shared (100.64/10) or unique-local address, or a name only a LAN resolves
    // (one label, .local, .lan, .internal, .home.arpa). Another name is not looked up, and counts as public.
    private static bool IsPrivate(Uri uri)
    {
        if (Literal(uri) is not { } address)
        {
            string host = uri.Host.TrimEnd('.');
            return !host.Contains('.', StringComparison.Ordinal)
                || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase);
        }
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
        }
        return (bytes[0] & 0xFE) == 0xFC || address.IsIPv6SiteLocal;
    }

    // The host as an address, an IPv4-mapped IPv6 one as the IPv4 address it is; null for a name.
    private static IPAddress? Literal(Uri uri)
    {
        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address))
        {
            return null;
        }
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    private static bool IsSeekOnly(string query) =>
        query.StartsWith("?seek=", StringComparison.Ordinal)
        && query.Length > "?seek=".Length
        && query["?seek=".Length..].All(c => char.IsAsciiDigit(c) || c == '.');
}
