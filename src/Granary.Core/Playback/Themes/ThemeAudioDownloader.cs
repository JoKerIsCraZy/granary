using System.Net;
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

    /// <summary>
    /// The theme client's own handler: it keeps no cookie a node sets, follows no redirect, and takes the body as it was
    /// sent, so the size limit counts what came over the wire.
    /// </summary>
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    /// <param name="routed">The server's capability says themes are routed, so a grant may name a proxy node's origin.</param>
    /// <exception cref="HttpRequestException">The URL is not one Granary fetches, or the answer is not a theme.</exception>
    public async Task<ThemeAudioSource> DownloadAsync(Uri server, ThemePlayback grant, bool routed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var url = PlaybackUrl.ResolveTheme(server, grant.Url, routed)
            ?? throw new HttpRequestException(HttpRequestError.InvalidResponse, "The server named a theme address Granary does not fetch.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var client = clients.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(HttpRequestError.Unknown, "The theme could not be fetched.", null, response.StatusCode);
        }
        if (response.Content.Headers.ContentLength > MaxBytes)
        {
            throw new HttpRequestException(HttpRequestError.InvalidResponse, "The theme is too large.");
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
                throw new HttpRequestException(HttpRequestError.InvalidResponse, "The theme is too large.");
            }
            audio.Write(buffer, 0, read);
        }
        if (audio.Length == 0)
        {
            throw new HttpRequestException(HttpRequestError.InvalidResponse, "The theme was empty.");
        }
        return audio.ToArray();
    }
}
