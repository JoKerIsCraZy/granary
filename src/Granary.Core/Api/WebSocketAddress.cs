namespace Granary.Core.Api;

/// <summary>A server socket's address: <c>wss://</c> beside https, <c>ws://</c> beside http, under the server's own path.</summary>
public static class WebSocketAddress
{
    /// <summary>
    /// <paramref name="path"/> (relative, already escaped) on <paramref name="server"/>. No credential, query or fragment of
    /// the server's address goes along: a socket's ticket travels as a protocol.
    /// </summary>
    public static Uri For(Uri server, string path)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var builder = new UriBuilder(new Uri(server.AbsoluteUri.TrimEnd('/') + "/"))
        {
            Scheme = server.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Query = "",
            Fragment = "",
            UserName = "",
            Password = "",
        };
        builder.Path = builder.Path.TrimEnd('/') + "/" + path.TrimStart('/');
        if (builder.Uri.IsDefaultPort)
        {
            builder.Port = -1;
        }
        return builder.Uri;
    }
}
