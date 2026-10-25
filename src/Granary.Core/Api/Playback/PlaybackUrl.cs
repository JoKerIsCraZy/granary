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

    private static bool IsSeekOnly(string query) =>
        query.StartsWith("?seek=", StringComparison.Ordinal)
        && query.Length > "?seek=".Length
        && query["?seek=".Length..].All(c => char.IsAsciiDigit(c) || c == '.');
}
