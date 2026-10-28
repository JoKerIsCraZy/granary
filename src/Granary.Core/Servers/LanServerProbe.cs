using System.Net.Http.Headers;
using System.Text.Json;

namespace Granary.Core.Servers;

/// <summary>The two unauthenticated reads that confirm an advertised server and name it.</summary>
public interface ILanServerProbe
{
    /// <summary><c>server_id</c> from <c>GET /api/v2/system/identity</c> at <paramref name="origin"/>, or null.</summary>
    Task<string?> GetServerIdAsync(Uri origin, CancellationToken cancellationToken);

    /// <summary><c>server_name</c> from <c>GET /api/v2/theme/branding</c> at <paramref name="origin"/>, or null.</summary>
    Task<string?> GetNameAsync(Uri origin, CancellationToken cancellationToken);
}

/// <summary>
/// Reads a found address on a client of its own, as Android's <c>LanServerProbe</c>: an address that only advertised
/// itself gets no credentials, no profile and none of this device's headers, even when it shares a saved server's
/// origin. Nobody asked for these reads, so redirects are not followed (one could point them at this device's
/// loopback) and an answer is read to at most 64 KB. Every failure is "no answer".
/// </summary>
public sealed class LanServerProbe(HttpClient http) : ILanServerProbe
{
    internal const int MaxBodyBytes = 64 * 1024;

    /// <summary>The handler the probe's client is built on: no redirects, no cookies, no proxy for a LAN address.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = LanServerDiscovery.ProbeTimeout,
    };

    public async Task<string?> GetServerIdAsync(Uri origin, CancellationToken cancellationToken) =>
        LanAdverts.UsableId(await ReadStringAsync(origin, "api/v2/system/identity", "server_id", cancellationToken).ConfigureAwait(false));

    public Task<string?> GetNameAsync(Uri origin, CancellationToken cancellationToken) =>
        ReadStringAsync(origin, "api/v2/theme/branding", "server_name", cancellationToken);

    private async Task<string?> ReadStringAsync(Uri origin, string path, string property, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, path));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBodyBytes)
            {
                return null;
            }
            byte[] body = await ReadCappedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (body.Length > MaxBodyBytes)
            {
                return null;
            }
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()?.Trim() is { Length: > 0 } text ? text : null
                    : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    // One byte past the cap tells an answer that is too long from one that just fits.
    private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[MaxBodyBytes + 1];
        int read = 0;
        while (read < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }
            read += count;
        }
        return buffer[..read];
    }
}
