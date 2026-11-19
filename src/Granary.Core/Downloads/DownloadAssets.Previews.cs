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
// when the download's revision or the server's layout changes; one that does not come is tried at the next refresh. Nothing
// here fails the fetch of the manifest, artwork and subtitles.
public sealed partial class DownloadAssets
{
    /// <summary>The largest set of previews kept; a 2-hour film's takes a few megabytes.</summary>
    public const long MaxPreviewSetBytes = 64L * 1024 * 1024;

    /// <summary>The cap a set of previews is held to; <see cref="MaxPreviewSetBytes"/>, lower for tests.</summary>
    internal long MaxPreviewBytes { get; init; } = MaxPreviewSetBytes;

    /// <summary>The previews are kept: what reads and fetches them was given.</summary>
    internal bool KeepsPreviews => watch is not null && http is not null;

    private async Task FetchPreviewsAsync(Uri server, StoredDownload download, OfflineManifest manifest, CancellationToken cancellationToken)
    {
        var record = download.Record;
        if (watch is null || http is null || (manifest.MediaFileId ?? record.MediaFileId) is not { Length: > 0 } fileId)
        {
            return;
        }
        try
        {
            var listed = await watch.GetTrickplayAsAccountProfileAsync(server, record.Scope.AccountId, record.Scope.ProfileId, record.MediaItemId,
                fileId, cancellationToken).ConfigureAwait(false);
            await KeepPreviewsAsync(server, download, listed, http, cancellationToken).ConfigureAwait(false);
        }
        catch (SiloApiException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            // The file has no previews; a set kept before stays, it shows the same file.
            logger.LogDebug("The file of download {DownloadId} has no seek-bar previews", record.DownloadId);
        }
        catch (Exception ex) when (ex is SiloApiException or HttpRequestException or JsonException or IOException or UnauthorizedAccessException
            or ProfileChangedException or ArgumentException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogInformation("The seek-bar previews of download {DownloadId} were not kept ({Failure}); they are tried at the next refresh",
                record.DownloadId, PreviewFailure(ex));
        }
    }

    private async Task KeepPreviewsAsync(Uri server, StoredDownload download, TrickplayManifest listed, IHttpClientFactory clients,
        CancellationToken cancellationToken)
    {
        string id = download.Record.DownloadId;
        var wanted = KeptPreviewsFile.Of(listed, download.Record.Revision);
        var sheets = listed.Sheets.OrderBy(s => s.Index).Select(s => ArtworkUrl.Resolve(server, s.Url)).ToList();
        if (!KeptPreviews.IsDrawable(wanted) || sheets.Any(s => s is null))
        {
            logger.LogInformation("The seek-bar previews of download {DownloadId} cannot be kept: the server's layout is not one Granary draws", id);
            return;
        }
        if (KeptSet(download) is { } kept && kept.SameSetAs(wanted) && (kept.TooLarge || HasSheets(download, kept.Sheets)))
        {
            return;
        }
        var staged = new List<string>();
        try
        {
            using var client = clients.CreateClient(ArtworkLoader.HttpClientName);
            long bytes = 0;
            for (int ordinal = 0; ordinal < sheets.Count; ordinal++)
            {
                string target = Path.Combine(download.Folder, $"{DownloadPaths.PreviewSheet(ordinal)}.{Guid.NewGuid():N}.staged");
                if (await FetchSheetAsync(client, sheets[ordinal]!, target, MaxPreviewBytes - bytes, cancellationToken).ConfigureAwait(false) is not { } size)
                {
                    logger.LogInformation("The seek-bar previews of download {DownloadId} are larger than {Cap} bytes; they are not kept", id,
                        MaxPreviewBytes);
                    CommitPreviews(download, wanted with { TooLarge = true }, []);
                    return;
                }
                staged.Add(target);
                bytes += size;
            }
            CommitPreviews(download, wanted with { Bytes = bytes }, staged);
            logger.LogInformation("Download {DownloadId} keeps {Sheets} seek-bar preview sheets ({Bytes} bytes)", id, staged.Count, bytes);
        }
        finally
        {
            foreach (string left in staged)
            {
                TryDelete(left);
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

    // The new set takes the old one's place: its description goes first, so a set half replaced is never taken for whole, and
    // comes back last, once every sheet is in place. Sheets of the old set past the new one's go.
    private static void CommitPreviews(StoredDownload download, KeptPreviewsFile set, IReadOnlyList<string> staged)
    {
        string description = Path.Combine(download.Folder, DownloadPaths.Previews);
        File.Delete(description);
        for (int ordinal = 0; ordinal < staged.Count; ordinal++)
        {
            string from = staged[ordinal];
            SafeFile.WriteWhole(Path.Combine(download.Folder, DownloadPaths.PreviewSheet(ordinal)), temp => File.Move(from, temp));
        }
        var current = Enumerable.Range(0, staged.Count).Select(DownloadPaths.PreviewSheet).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(download.Folder, "tp-*.jpg"))
        {
            if (!current.Contains(Path.GetFileName(file)))
            {
                File.Delete(file);
            }
        }
        byte[] json = KeptPreviews.Write(set);
        SafeFile.WriteWhole(description, temp => File.WriteAllBytes(temp, json));
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

    private static bool HasSheets(StoredDownload download, int count) =>
        Enumerable.Range(0, count).All(ordinal => File.Exists(Path.Combine(download.Folder, DownloadPaths.PreviewSheet(ordinal))));

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
