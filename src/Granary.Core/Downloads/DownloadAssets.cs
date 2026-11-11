using System.Net;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Granary.Core.IO;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>What a fetch kept: the manifest, and the artwork and subtitles in the folder now.</summary>
/// <param name="ArtworkFiles">Each kept artwork's file name by kind.</param>
/// <param name="SubtitleRevisions">Each kept subtitle's revision by its reference (<c>external:0</c>).</param>
public sealed record FetchedAssets(OfflineManifest Manifest, IReadOnlyDictionary<string, string> ArtworkFiles,
    IReadOnlyDictionary<string, string> SubtitleRevisions);

/// <summary>
/// A download's manifest, artwork and subtitles, kept beside its media so it plays and shows without the server
/// (docs/architecture.md, "What the server says"). The manifest is kept as the server sent it; artwork and subtitles that do not
/// come are logged and left out, to be tried at the next refresh. Only this download's own asset paths are asked for
/// (<see cref="DownloadsApi.AssetPath"/>): they carry the profile's credentials.
/// </summary>
public sealed class DownloadAssets(DownloadsApi api, TimeProvider time, ILogger<DownloadAssets> logger)
{
    /// <summary>The artwork kept: an episode's poster is its still, and its series' poster comes as well.</summary>
    public static readonly IReadOnlyList<string> ArtworkKinds = ["poster", "backdrop", "series_poster"];

    /// <summary>An asset that brings no byte for this long is given up.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The largest artwork or subtitle kept; a bitmap subtitle of a long film stays well below.</summary>
    public const long MaxAssetBytes = 512L * 1024 * 1024;

    private const int BufferSize = 81920;

    /// <summary>The cap an asset is held to; <see cref="MaxAssetBytes"/>, lower for tests.</summary>
    internal long MaxBytes { get; init; } = MaxAssetBytes;

    /// <summary>
    /// Reads the manifest and keeps it in the folder, then fetches the artwork the folder lacks and the subtitles that are new
    /// or whose revision changed.
    /// </summary>
    /// <exception cref="JsonException">The server's manifest is not this download's.</exception>
    /// <exception cref="SiloApiException">The manifest was refused.</exception>
    /// <exception cref="HttpRequestException">The manifest did not come.</exception>
    /// <exception cref="IOException">The manifest could not be written to the folder.</exception>
    /// <exception cref="ProfileChangedException">The server acts as another profile now.</exception>
    public async Task<FetchedAssets> FetchAsync(Uri server, StoredDownload download, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(download);
        var record = download.Record;
        byte[] bytes = await api.GetManifestAsync(server, record.Scope.ProfileId, record.DownloadId, cancellationToken).ConfigureAwait(false);
        var manifest = OfflineManifest.Read(bytes);
        if (manifest.DownloadId != record.DownloadId)
        {
            throw new JsonException("The manifest is another download's.");
        }
        SafeFile.WriteWhole(Path.Combine(download.Folder, DownloadPaths.Manifest), temp => File.WriteAllBytes(temp, bytes));
        var artwork = await FetchArtworkAsync(server, download, manifest, cancellationToken).ConfigureAwait(false);
        var subtitles = await FetchSubtitlesAsync(server, download, manifest, cancellationToken).ConfigureAwait(false);
        return new FetchedAssets(manifest, artwork, subtitles);
    }

    private async Task<IReadOnlyDictionary<string, string>> FetchArtworkAsync(Uri server, StoredDownload download, OfflineManifest manifest,
        CancellationToken cancellationToken)
    {
        var record = download.Record;
        var kept = new Dictionary<string, string>();
        foreach (string kind in ArtworkKinds)
        {
            if (record.ArtworkFiles.TryGetValue(kind, out string? name) && File.Exists(Path.Combine(download.Folder, name)))
            {
                kept[kind] = name;
                continue;
            }
            if (!manifest.ArtworkUrls.TryGetValue(kind, out string? url))
            {
                continue;
            }
            if (DownloadsApi.AssetPath(record.DownloadId, url) is not { } path)
            {
                logger.LogWarning("The {Kind} of download {DownloadId} is not this download's artwork; it is left out", kind, record.DownloadId);
                continue;
            }
            if (await FetchOneAsync(server, download, path, kind, response => DownloadPaths.ArtworkName(kind, response.Content.Headers.ContentType?.MediaType),
                cancellationToken).ConfigureAwait(false) is { } saved)
            {
                DeleteOtherArtwork(download, kind, saved);
                kept[kind] = saved;
            }
        }
        return kept;
    }

    // A subtitle is fetched when it is new, its file is gone, or its revision changed; one that does not come keeps what it was.
    // Its file is named after its reference, so a manifest listing the subtitles in another order changes nothing.
    private async Task<IReadOnlyDictionary<string, string>> FetchSubtitlesAsync(Uri server, StoredDownload download, OfflineManifest manifest,
        CancellationToken cancellationToken)
    {
        var record = download.Record;
        var revisions = new Dictionary<string, string>();
        for (int index = 0; index < manifest.Subtitles.Count; index++)
        {
            var subtitle = manifest.Subtitles[index];
            if (DownloadsApi.AssetPath(record.DownloadId, subtitle.FetchUrl) is not { } path)
            {
                logger.LogWarning("Subtitle {Index} of download {DownloadId} is not this download's; it is left out", index, record.DownloadId);
                continue;
            }
            string reference = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
            string name = DownloadPaths.SubtitleName(reference, subtitle.Format);
            string? known = record.SubtitleRevisions.GetValueOrDefault(reference);
            bool there = File.Exists(Path.Combine(download.Folder, name));
            if (there && (subtitle.Revision is null || subtitle.Revision == known))
            {
                if (known is not null)
                {
                    revisions[reference] = known;
                }
                continue;
            }
            if (await FetchOneAsync(server, download, path, "subtitle", _ => name, cancellationToken).ConfigureAwait(false) is not null)
            {
                if (subtitle.Revision is not null)
                {
                    revisions[reference] = subtitle.Revision;
                }
            }
            else if (there && known is not null)
            {
                revisions[reference] = known;
            }
        }
        return revisions;
    }

    // One asset into the folder under the name its answer gives it; null when it did not come. A profile change or a stop ends
    // the whole fetch.
    private async Task<string?> FetchOneAsync(Uri server, StoredDownload download, string path, string what, Func<HttpResponseMessage, string> nameOf,
        CancellationToken cancellationToken)
    {
        var record = download.Record;
        try
        {
            using var response = await api.OpenAssetAsync(server, record.Scope.ProfileId, path, null, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                // A 404 will not change; a busy store (503) is tried at the next refresh.
                logger.LogInformation("The {What} of download {DownloadId} was answered {Status}; it is left out", what, record.DownloadId,
                    (int)response.StatusCode);
                return null;
            }
            string name = nameOf(response);
            await SaveAsync(response, Path.Combine(download.Folder, name), cancellationToken).ConfigureAwait(false);
            return name;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Neither the message of a network error (the server) nor a file error's (the Windows user's folder) is logged.
            logger.LogInformation("The {What} of download {DownloadId} did not come ({Failure}); it is left out", what, record.DownloadId,
                ex is HttpRequestException http ? http.HttpRequestError.ToString() : DownloadLog.Failure(ex));
            return null;
        }
    }

    // The body into a file beside the target, then moved over it whole; at most MaxAssetBytes, and given up after a stall.
    private async Task SaveAsync(HttpResponseMessage response, string target, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxBytes)
        {
            throw new HttpRequestException(HttpRequestError.InvalidResponse, "The asset is larger than Granary keeps.");
        }
        string arriving = $"{target}.{Guid.NewGuid():N}.arriving";
        using var stall = new CancellationTokenSource(StallTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stall.Token);
        try
        {
            await using (var file = new FileStream(arriving, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                await using var body = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                byte[] buffer = new byte[BufferSize];
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    int read = await body.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                    if (file.Length + read > MaxBytes)
                    {
                        throw new HttpRequestException(HttpRequestError.InvalidResponse, "The asset is larger than Granary keeps.");
                    }
                    await file.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                }
            }
            SafeFile.WriteWhole(target, temp => File.Move(arriving, temp));
        }
        catch (OperationCanceledException) when (stall.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException(HttpRequestError.ResponseEnded, "The asset stopped arriving.");
        }
        finally
        {
            TryDelete(arriving);
        }
    }

    // A kind's artwork under another extension than the one kept now goes.
    private void DeleteOtherArtwork(StoredDownload download, string kind, string kept)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(download.Folder, DownloadPaths.Segment(kind) + ".*"))
            {
                string name = Path.GetFileName(file);
                if (name != kept && DownloadPaths.IsArtworkName(kind, name))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug("An older {Kind} of download {DownloadId} stays ({Failure})", kind, download.Record.DownloadId, DownloadLog.Failure(ex));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left beside under its own name, where it is in no write's way.
        }
    }
}
