using System.Net;
using System.Net.Sockets;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Playback;

namespace Granary.Core.Playback.Themes;

/// <summary>A theme's audio, whole, and how the server sent it.</summary>
/// <param name="Converted">The server's AAC conversion: it cannot loop in place, so a loop asks for a new grant (the web's rule).</param>
public sealed record ThemeAudioSource(ReadOnlyMemory<byte> Audio, string? ContentType, bool Converted);

/// <summary>
/// Fetches a granted theme whole, as the Apple app reads a converted one: a theme is short and the server caps its bitrate,
/// and a converted stream has no length and no byte ranges for a player to stream. Its URL carries its own token, so the
/// request goes without credentials, never follows a redirect and is kept out of the log; only Core ever holds the URL.
/// </summary>
public sealed class ThemeAudioDownloader(IHttpClientFactory clients)
{
    public const string HttpClientName = "theme-audio";

    /// <summary>Anything larger is not a theme (the Apple app's limit).</summary>
    public const int MaxBytes = 32 * 1024 * 1024;

    /// <summary>The whole download, body included.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // A proxy node's request carries its connection rule, which the handler applies to the addresses the name resolves to.
    private static readonly HttpRequestOptionsKey<ThemeNode> NodeOption = new("granary.theme-node");

    /// <summary>
    /// The theme client's own handler: it keeps no cookie a node sets, follows no redirect, and takes the body as it was
    /// sent, so the size limit counts what came over the wire. A node's connection goes only to an address its rule takes
    /// (<see cref="ThemeNode"/>); the server's own origin connects as it resolves.
    /// </summary>
    internal static SocketsHttpHandler CreateHandler() => CreateHandler(Dns.GetHostAddressesAsync);

    /// <param name="resolve">Looks a node's name up; tests answer it themselves.</param>
    internal static SocketsHttpHandler CreateHandler(Func<string, CancellationToken, Task<IPAddress[]>> resolve) => new()
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        // A theme is one request. With either set to zero the handler keeps no connection for the next one, so a node's
        // connection, checked against its rule as it opened, never serves a request made under another rule.
        PooledConnectionLifetime = TimeSpan.Zero,
        PooledConnectionIdleTimeout = TimeSpan.Zero,
        ConnectCallback = (context, cancellationToken) => ConnectAsync(context, resolve, cancellationToken),
    };

    // A name that resolves to this PC, or a private address the server did not vouch for, is refused here, where the
    // address is known (a name looked up again, a rebinding, a wildcard DNS name). TLS goes on to check the node's name.
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (!context.InitialRequestMessage.Options.TryGetValue(NodeOption, out var node))
            {
                await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            string host = NetworkAddresses.Normalize(endpoint.Host);
            var url = context.InitialRequestMessage.RequestUri!;
            if (!host.Equals(NetworkAddresses.HostOf(url), StringComparison.OrdinalIgnoreCase) || endpoint.Port != url.Port)
            {
                // Through a proxy, the proxy would look the name up, where this rule cannot see it.
                throw new ThemeRefusedException("A theme's node is reached directly or not at all.");
            }
            IPAddress[] addresses = [.. (await resolve(host, cancellationToken).ConfigureAwait(false)).Where(node.Takes)];
            if (addresses.Length == 0)
            {
                throw new ThemeRefusedException("The theme's node is at an address Granary does not fetch.");
            }
            await socket.ConnectAsync(addresses, endpoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <param name="origins">Where the server lets the audio come from: its own origin, or also a proxy node's.</param>
    /// <exception cref="HttpRequestException">The URL is not one Granary fetches, or the answer is not a theme.</exception>
    public async Task<ThemeAudioSource> DownloadAsync(Uri server, ThemePlayback grant, ThemeOrigins origins, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var address = PlaybackUrl.ResolveTheme(server, grant.Url, origins)
            ?? throw new ThemeRefusedException("The server named a theme address Granary does not fetch.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, address.Url);
        if (address.Node is { } node)
        {
            request.Options.Set(NodeOption, node);
        }
        var client = clients.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(HttpRequestError.Unknown, "The theme could not be fetched.", null, response.StatusCode);
        }
        if (response.Content.Headers.ContentLength > MaxBytes)
        {
            throw new ThemeRefusedException("The theme is too large.");
        }
        byte[] audio = await ReadAsync(response.Content, timeout.Token).ConfigureAwait(false);
        return new ThemeAudioSource(audio, grant.ContentType ?? response.Content.Headers.ContentType?.MediaType, grant.IsConverted);
    }

    // A converted stream names no length, so the limit is kept as it comes.
    private static async Task<byte[]> ReadAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var audio = new MemoryStream();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (audio.Length + read > MaxBytes)
            {
                throw new ThemeRefusedException("The theme is too large.");
            }
            audio.Write(buffer, 0, read);
        }
        if (audio.Length == 0)
        {
            throw new ThemeRefusedException("The theme was empty.");
        }
        return audio.ToArray();
    }
}
