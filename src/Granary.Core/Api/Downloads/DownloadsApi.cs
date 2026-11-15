using System.Net;
using System.Net.Http.Headers;
using Granary.Core.Api.Playback;

namespace Granary.Core.Api.Downloads;

/// <summary>
/// The server's managed downloads (downloads-api.md, sections 3 to 6; docs/api-contract.md, 14). Granary sends its device id
/// on every request, so each entry belongs to this account, profile and PC. Every call is bound to the profile its download
/// belongs to: while the server acts as another one, it throws <see cref="ProfileChangedException"/> unsent.
/// </summary>
/// <param name="time">The clock of <see cref="ManifestStallTimeout"/>; the system's when not given.</param>
public sealed class DownloadsApi(SiloHttp http, TimeProvider? time = null)
{
    /// <summary>A manifest that brings no byte for this long is given up.</summary>
    public static readonly TimeSpan ManifestStallTimeout = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The largest manifest the server sends (it answers 413 above it); a longer answer is refused the same way.</summary>
    public const int MaxManifestBytes = 1 << 20;

    // The server's largest page of the registry.
    private const int PageLimit = 100;

    private const string Downloads = "api/v2/downloads/";

    public Task<DownloadCapability> GetCapabilityAsync(Uri server, string profileId, CancellationToken cancellationToken) =>
        http.GetAsProfileAsync(server, "api/v2/capabilities/downloads", profileId, DownloadsJsonContext.Default.DownloadCapability, cancellationToken);

    /// <summary>Registers one film or episode (202). Never sent twice: after an uncertain answer, read the registry instead.</summary>
    public Task<DownloadCreated> CreateAsync(Uri server, string profileId, CreateDownloadBody body, CancellationToken cancellationToken) =>
        http.SendAsProfileAsync(HttpMethod.Post, server, "api/v2/downloads", profileId, body, DownloadsJsonContext.Default.CreateDownloadBody,
            DownloadsJsonContext.Default.DownloadCreated, cancellationToken);

    /// <summary>One page of this profile's and PC's entries, newest first; <paramref name="cursor"/> continues a listing.</summary>
    public Task<DownloadEntryPage> ListAsync(Uri server, string profileId, string? cursor, CancellationToken cancellationToken)
    {
        string path = SiloHttp.Invariant($"api/v2/downloads?limit={PageLimit}") + (cursor is null ? "" : "&cursor=" + SiloHttp.Escape(cursor));
        return http.GetAsProfileAsync(server, path, profileId, DownloadsJsonContext.Default.DownloadEntryPage, cancellationToken);
    }

    /// <summary>Tells the server what the PC holds now; an older or equal event leaves the entry as it is (200 either way).</summary>
    public Task<DownloadEntry> ReportAsync(Uri server, string profileId, string downloadId, DownloadStatusEvent statusEvent,
        CancellationToken cancellationToken) =>
        http.SendAsProfileAsync(HttpMethod.Patch, server, Entry(downloadId), profileId, statusEvent, DownloadsJsonContext.Default.DownloadStatusEvent,
            DownloadsJsonContext.Default.DownloadEntry, cancellationToken);

    /// <summary>Asks for a finished download's prepared file again, after the file route said <c>prepared_file_expired</c>.</summary>
    public Task<DownloadEntry> PrepareAsync(Uri server, string profileId, string downloadId, CancellationToken cancellationToken) =>
        http.SendAsProfileAsync(HttpMethod.Post, server, Entry(downloadId) + "/prepare", profileId, DownloadsJsonContext.Default.DownloadEntry,
            cancellationToken);

    /// <summary>Removes the entry (204). One the server no longer has (404) is gone already, which is what was asked.</summary>
    public async Task DeleteAsync(Uri server, string profileId, string downloadId, CancellationToken cancellationToken)
    {
        try
        {
            await http.SendAsProfileAsync(HttpMethod.Delete, server, Entry(downloadId), profileId, cancellationToken).ConfigureAwait(false);
        }
        catch (SiloApiException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            // Deleted before, or never registered for this profile and PC.
        }
    }

    /// <summary>The manifest's bytes as the server sent them, to keep verbatim; at most <see cref="MaxManifestBytes"/> (else 413).</summary>
    /// <exception cref="HttpRequestException">
    /// <see cref="HttpRequestError.ResponseEnded"/>: no byte came for <see cref="ManifestStallTimeout"/>.
    /// </exception>
    public async Task<byte[]> GetManifestAsync(Uri server, string profileId, string downloadId, CancellationToken cancellationToken)
    {
        using var stall = new CancellationTokenSource(ManifestStallTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stall.Token);
        try
        {
            return await ReadManifestAsync(server, profileId, downloadId, stall, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException(HttpRequestError.ResponseEnded, "The manifest stopped arriving.");
        }
    }

    private async Task<byte[]> ReadManifestAsync(Uri server, string profileId, string downloadId, CancellationTokenSource stall,
        CancellationToken cancellationToken)
    {
        using var response = await http.OpenAsProfileAsync(server, Entry(downloadId) + "/manifest", profileId, null, null, cancellationToken)
            .ConfigureAwait(false);
        await SiloHttp.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaxManifestBytes)
        {
            throw TooLarge();
        }
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[81920];
        while (true)
        {
            stall.CancelAfter(ManifestStallTimeout);
            int read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return bytes.ToArray();
            }
            if (bytes.Length + read > MaxManifestBytes)
            {
                throw TooLarge();
            }
            bytes.Write(buffer, 0, read);
        }
    }

    /// <summary>
    /// A download's artwork or subtitle at <paramref name="relativePath"/> (from <see cref="AssetPath"/>), whatever its status.
    /// <paramref name="etag"/> is the version kept: an unchanged subtitle answers 304.
    /// </summary>
    public Task<HttpResponseMessage> OpenAssetAsync(Uri server, string profileId, string relativePath, string? etag, CancellationToken cancellationToken) =>
        http.OpenAsProfileAsync(server, relativePath, profileId, null, etag, cancellationToken);

    /// <summary>
    /// The media file, whatever its status: from <paramref name="range"/>'s offset to the end with <c>If-Range</c>, or whole.
    /// Always from the server itself, as Silo's apps take it (<c>/file</c>, never <c>/file-proxy</c> and its proxy nodes): on the
    /// local network that is the local route. The caller reads and disposes the answer.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="range"/>'s validator is not one <c>If-Range</c> takes.</exception>
    public Task<HttpResponseMessage> OpenFileAsync(Uri server, string profileId, string downloadId, ByteRange? range,
        CancellationToken cancellationToken) =>
        http.OpenAsProfileAsync(server, Entry(downloadId) + "/file", profileId, range, null, cancellationToken);

    /// <summary>
    /// A manifest's <c>artwork_urls</c> or <c>subtitles[].fetch_url</c> as a path under the saved address, or null when it is
    /// not this download's artwork or subtitle. The path is taken from <c>api/v2/downloads/</c> on, so a reverse proxy's prefix is
    /// rebuilt under the saved address; it must name this download, then <c>artwork</c> or <c>subtitles</c> and one more
    /// segment. Absolute URLs, dot segments, encoded slashes, queries and anything else are refused: these requests carry the
    /// profile's credentials.
    /// </summary>
    public static string? AssetPath(string downloadId, string? fetchUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadId);
        if (string.IsNullOrEmpty(fetchUrl) || !fetchUrl.StartsWith('/') || fetchUrl.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }
        if (fetchUrl.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '\\' or '?' or '#')
            || fetchUrl.Contains("%2e", StringComparison.OrdinalIgnoreCase)
            || fetchUrl.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || fetchUrl.Contains("%5c", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string[] all = fetchUrl.Split('/');
        if (all.Any(segment => segment is "." or ".."))
        {
            return null;
        }
        int start = fetchUrl.IndexOf("/" + Downloads, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }
        string path = fetchUrl[(start + 1)..];
        string[] segments = path.Split('/');
        // api, v2, downloads, the download, artwork or subtitles, the asset.
        bool valid = segments.Length == 6
            && segments[3] == SiloHttp.Escape(downloadId)
            && segments[4] is ("artwork" or "subtitles")
            && segments[5].Length > 0;
        return valid ? path : null;
    }

    private static string Entry(string downloadId) => Downloads + SiloHttp.PathSegment(downloadId, nameof(downloadId));

    private static SiloApiException TooLarge() =>
        new((int)HttpStatusCode.RequestEntityTooLarge, ApiProblem.FromStatus((int)HttpStatusCode.RequestEntityTooLarge));
}
