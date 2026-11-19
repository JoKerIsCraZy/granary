using System.Net;
using System.Text.Json;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;
using Granary.Core.Api.Images;
using Granary.Core.Api.Playback;
using Granary.Core.Artwork;
using Granary.Core.IO;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

// The seek-bar previews kept with a download (docs/architecture.md, "Playing a download"), so its seek bar shows pictures
// without the server. They are the previews of the file the download was made from (the manifest's media_file_id), whose
// timeline is the download's, read as the download's own account and profile. Their sheets are signed addresses that need no
// credentials: they are fetched as artwork is, never through SiloHttp. A set is kept whole or not at all, and fetched again
// when the download's revision or the server's layout changes; one that does not come is tried at the next refresh. They are
// fetched beside the media's transfer and after a refresh, never in the way of either (DownloadQueue).
public sealed partial class DownloadAssets
{
    /// <summary>The largest set of previews kept; a 2-hour film's takes a few megabytes.</summary>
    public const long MaxPreviewSetBytes = 64L * 1024 * 1024;

    /// <summary>The longest a set of previews takes to come, its read and every sheet together; one slower is tried again later.</summary>
    public static readonly TimeSpan PreviewDeadline = TimeSpan.FromSeconds(60);

    /// <summary>The cap a set of previews is held to; <see cref="MaxPreviewSetBytes"/>, lower for tests.</summary>
    internal long MaxPreviewBytes { get; init; } = MaxPreviewSetBytes;

    /// <summary>The previews are kept: what reads and fetches them was given.</summary>
    internal bool KeepsPreviews => watch is not null && http is not null;

    /// <summary>
    /// Keeps the seek-bar previews of the file <paramref name="download"/> was made from, after the manifest kept in its folder,
    /// within <see cref="PreviewDeadline"/>. Never throws but for <paramref name="cancellationToken"/>: what does not come is
    /// logged, and the download stays as it is.
    /// </summary>
    public async Task FetchPreviewsAsync(Uri server, StoredDownload download, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(download);
        if (watch is null || http is null)
        {
            return;
        }
        var record = download.Record;
        using var deadline = new CancellationTokenSource(PreviewDeadline, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var manifest = OfflineManifest.Read(await File.ReadAllBytesAsync(Path.Combine(download.Folder, DownloadPaths.Manifest), linked.Token)
                .ConfigureAwait(false));
            if (manifest.DownloadId != record.DownloadId || manifest.Revision != record.Revision
                || (manifest.MediaFileId ?? record.MediaFileId) is not { Length: > 0 } fileId)
            {
                return;
            }
            await FetchPreviewsAsync(server, download, fileId, watch, http, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SiloApiException or HttpRequestException or JsonException or IOException or UnauthorizedAccessException
            or ProfileChangedException or ArgumentException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogInformation("The seek-bar previews of download {DownloadId} were not kept ({Failure}); they are tried at the next refresh",
                record.DownloadId, deadline.IsCancellationRequested ? "took too long" : PreviewFailure(ex));
        }
    }

    private async Task FetchPreviewsAsync(Uri server, StoredDownload download, string fileId, WatchApi watchApi, IHttpClientFactory clients,
        CancellationToken cancellationToken)
    {
        var record = download.Record;
        TrickplayManifest? listed;
        try
        {
            listed = await watchApi.GetTrickplayAsAccountProfileAsync(server, record.Scope.AccountId, record.Scope.ProfileId, record.MediaItemId,
                fileId, cancellationToken).ConfigureAwait(false);
        }
        catch (SiloApiException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            listed = null;
        }
        if (listed is null)
        {
            // The file has no previews. A set kept before for these bytes stays, it shows the same file; one of other bytes
            // (a replaced revision leaves it in the folder) goes.
            logger.LogDebug("The file of download {DownloadId} has no seek-bar previews", record.DownloadId);
            if (KeptSet(download) is { } kept && (kept.Revision != record.Revision || kept.FileId != fileId))
            {
                PublishPreviews(download, null);
            }
            return;
        }
        // Kept under the file asked for, which a play compares with the manifest's.
        await KeepPreviewsAsync(server, download, listed with { FileId = fileId }, clients, cancellationToken).ConfigureAwait(false);
    }
    private async Task KeepPreviewsAsync(Uri server, StoredDownload download, TrickplayManifest listed, IHttpClientFactory clients,
        CancellationToken cancellationToken)
    {
        string id = download.Record.DownloadId;
        // A list with an empty entry is no layout: leaving it out would shift the sheets after it.
        if (listed.Sheets.Any(s => s is null))
        {
            logger.LogInformation("The seek-bar previews of download {DownloadId} cannot be kept: the server's list has an empty sheet", id);
            return;
        }
        var wanted = KeptPreviewsFile.Of(listed, download.Record.Revision);
        var sheets = listed.Sheets.OrderBy(s => s.Index).Select(s => ArtworkUrl.Resolve(server, s.Url)).ToList();
        if (!KeptPreviews.IsDrawable(wanted) || sheets.Any(s => s is null))
        {
            logger.LogInformation("The seek-bar previews of download {DownloadId} cannot be kept: the server's layout is not one Granary draws", id);
            return;
        }
        if (KeptSet(download) is { } kept && kept.SameSetAs(wanted) && (kept.TooLarge || HasSheets(download, kept)))
        {
            return;
        }
        // A generation of its own: its sheets are never another set's, so nothing a play shows or a set kept now is overwritten,
        // and the set only counts once its description names it.
        var set = wanted with { Generation = Guid.NewGuid().ToString("N")[..8] };
        int fetched = 0;
        bool published = false;
        try
        {
            using var client = clients.CreateClient(ArtworkLoader.HttpClientName);
            long bytes = 0;
            for (; fetched < sheets.Count; fetched++)
            {
                string target = Path.Combine(download.Folder, DownloadPaths.PreviewSheet(set.Generation, fetched));
                if (await FetchSheetAsync(client, sheets[fetched]!, target, MaxPreviewBytes - bytes, cancellationToken).ConfigureAwait(false) is not { } size)
                {
                    logger.LogInformation("The seek-bar previews of download {DownloadId} are larger than {Cap} bytes; they are not kept", id,
                        MaxPreviewBytes);
                    PublishPreviews(download, wanted with { TooLarge = true });
                    return;
                }
                bytes += size;
            }
            PublishPreviews(download, set with { Bytes = bytes });
            published = true;
            logger.LogInformation("Download {DownloadId} keeps {Sheets} seek-bar preview sheets ({Bytes} bytes)", id, fetched, bytes);
        }
        finally
        {
            if (!published)
            {
                // What came of a set that is not kept goes; the one kept before stays as it was.
                for (int ordinal = 0; ordinal <= Math.Min(fetched, sheets.Count - 1); ordinal++)
                {
                    TryDelete(Path.Combine(download.Folder, DownloadPaths.PreviewSheet(set.Generation, ordinal)));
                }
            }
        }
    }
    // One sheet into target; its size, or null when it would pass the bytes left of the cap. One that is not answered 200 ends
    // the set, as a stall does: a host that does not answer costs one wait, not one per sheet.
    private async Task<long?> FetchSheetAsync(HttpClient client, Uri sheet, string target, long left, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, sheet);
        // The artwork client waits as long as it takes; an answer is waited for as long as a stalled body is.
        using var stall = new CancellationTokenSource(StallTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stall.Token);
        HttpResponseMessage answered;
        try
        {
            answered = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stall.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException(HttpRequestError.ConnectionError, "A sheet was not answered.");
        }
        using var response = answered;
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException(HttpRequestError.InvalidResponse, "A sheet was not answered.", null, response.StatusCode);
        }
        try
        {
            return await SaveAsync(response, target, left, cancellationToken).ConfigureAwait(false);
        }
        catch (TooLargeException)
        {
            return null;
        }
    }

    // The description is written whole, last: from then on it names the new set, or none (null), and a failure before leaves
    // the set kept before as it was. The set it named before stays beside the new one: a play that began with it reads its
    // sheets only as it shows them, and goes on finding them. Older generations go, as do the pieces of sheets that never
    // came whole, as far as nothing holds them; a set dropped (null) goes whole.
    private void PublishPreviews(StoredDownload download, KeptPreviewsFile? set)
    {
        string description = Path.Combine(download.Folder, DownloadPaths.Previews);
        var before = set is null ? null : KeptSet(download);
        if (set is null)
        {
            File.Delete(description);
        }
        else
        {
            byte[] json = KeptPreviews.Write(set);
            SafeFile.WriteWhole(description, temp => File.WriteAllBytes(temp, json));
        }
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var named in new[] { set, before })
        {
            if (named is { TooLarge: false } && KeptPreviews.IsGeneration(named.Generation))
            {
                kept.UnionWith(Enumerable.Range(0, Math.Min(named.Sheets, KeptPreviews.MaxSheets))
                    .Select(ordinal => DownloadPaths.PreviewSheet(named.Generation, ordinal)));
            }
        }
        var leftovers = Directory.EnumerateFiles(download.Folder, "tp-*.jpg").Where(file => !kept.Contains(Path.GetFileName(file)))
            .Concat(Directory.EnumerateFiles(download.Folder, "tp-*.arriving")).ToList();
        foreach (string file in leftovers)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug("An older seek-bar preview sheet of download {DownloadId} stays ({Failure})", download.Record.DownloadId,
                    DownloadLog.Failure(ex));
            }
        }
    }

    // The set kept now; null when there is none or it does not read, which a fetch replaces.
    private KeptPreviewsFile? KeptSet(StoredDownload download)
    {
        try
        {
            return KeptPreviews.ReadFile(download.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogDebug("The kept seek-bar previews of download {DownloadId} do not read ({Failure})", download.Record.DownloadId, DownloadLog.Failure(ex));
            return null;
        }
    }

    private static bool HasSheets(StoredDownload download, KeptPreviewsFile set) =>
        Enumerable.Range(0, set.Sheets).All(ordinal => File.Exists(Path.Combine(download.Folder, DownloadPaths.PreviewSheet(set.Generation, ordinal))));

    // The status, or the kind of failure: never an address, which carries its signature.
    private static string PreviewFailure(Exception ex) => ex switch
    {
        SiloApiException api => api.Status.ToString(System.Globalization.CultureInfo.InvariantCulture),
        HttpRequestException { StatusCode: { } status } => ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture),
        HttpRequestException http => http.HttpRequestError.ToString(),
        _ => DownloadLog.Failure(ex),
    };

    /// <summary>An asset, or a set of previews, larger than Granary keeps.</summary>
    private sealed class TooLargeException() : HttpRequestException(HttpRequestError.InvalidResponse, "The asset is larger than Granary keeps.");
}
