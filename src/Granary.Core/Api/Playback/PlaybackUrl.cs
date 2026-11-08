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
    /// (<c>authorized_media_origins_v1</c>, <see cref="ResolveStream"/>): HTTPS (or HTTP beside an HTTP server), no user info,
    /// no fragment, and never this machine, a link-local, multicast or unspecified address, however written (<see cref="NetworkAddresses"/>).
    /// A server that authorizes media origins vouches for the node, a private address included; without that, a private or
    /// unique-local node is taken only from a server that is on a private network itself. It carries its own token, so it is
    /// fetched without credentials. A node's connection is checked again on the addresses its name resolves to
    /// (<see cref="ThemeAudioAddress.Node"/>).
    /// </summary>
    public static ThemeAudioAddress? ResolveTheme(Uri server, string? value, ThemeOrigins origins)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (Resolve(server, value) is { } onServer)
        {
            return IsPlain(onServer.OriginalString) ? new ThemeAudioAddress(onServer, Node: null) : null;
        }
        if (origins == ThemeOrigins.Server || !IsElsewhere(server, value) || !IsPlain(value!)
            || !Uri.TryCreate(value, UriKind.Absolute, out var node))
        {
            return null;
        }
        // As a video stream's node: HTTPS, or plain HTTP beside a plain HTTP server, never HTTP beside an HTTPS one.
        bool secure = node.Scheme == Uri.UriSchemeHttps || (node.Scheme == Uri.UriSchemeHttp && server.Scheme == Uri.UriSchemeHttp);
        if (!secure || node.UserInfo.Length > 0 || node.Fragment.Length > 0)
        {
            return null;
        }
        string host = NetworkAddresses.HostOf(node);
        bool isPrivate;
        if (NetworkAddresses.IsLocalhostName(host))
        {
            return null;
        }
        if (NetworkAddresses.Parse(host) is { } address)
        {
            if (NetworkAddresses.IsNeverANode(address))
            {
                return null;
            }
            isPrivate = NetworkAddresses.IsPrivate(address);
        }
        else
        {
            isPrivate = NetworkAddresses.IsLanName(host);
        }
        bool takesPrivate = origins == ThemeOrigins.Authorized || IsOnPrivateNetwork(server);
        return !isPrivate || takesPrivate ? new ThemeAudioAddress(new Uri(node.AbsoluteUri), new ThemeNode(takesPrivate)) : null;
    }

    // The server itself is on a private network: by its address, this PC's included, or by a name only a LAN resolves.
    private static bool IsOnPrivateNetwork(Uri server)
    {
        string host = NetworkAddresses.HostOf(server);
        if (NetworkAddresses.IsLocalhostName(host))
        {
            return true;
        }
        return NetworkAddresses.Parse(host) is { } address
            ? NetworkAddresses.IsPrivate(address) || IPAddress.IsLoopback(address)
            : NetworkAddresses.IsLanName(host);
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

    // A node is somewhere else on the network: not this machine, however it is written, not a link-local or cloud metadata
    // address.
    private static bool IsRemote(Uri node)
    {
        string host = NetworkAddresses.HostOf(node);
        if (node.IsLoopback || NetworkAddresses.IsLocalhostName(host))
        {
            return false;
        }
        return NetworkAddresses.Parse(host) is not { } address || !NetworkAddresses.IsNeverANode(address);
    }

    private static bool IsSeekOnly(string query) =>
        query.StartsWith("?seek=", StringComparison.Ordinal)
        && query.Length > "?seek=".Length
        && query["?seek=".Length..].All(c => char.IsAsciiDigit(c) || c == '.');
}
