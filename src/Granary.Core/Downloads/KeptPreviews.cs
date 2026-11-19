using System.Text.Json;
using System.Text.Json.Serialization;
using Granary.Core.Api.Downloads;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>
/// The seek-bar previews kept in a download's folder (<see cref="DownloadPaths.Previews"/>), as the server laid them out for the
/// file the download was made from: its file, the download's revision they were fetched for, and how many sheets
/// (<see cref="DownloadPaths.PreviewSheet"/>) there are. <see cref="TooLarge"/> remembers a set passed over for its size, so it is
/// not fetched again while the server's layout stays the same.
/// </summary>
internal sealed record KeptPreviewsFile
{
    public long Revision { get; init; }

    public string FileId { get; init => field = value ?? ""; } = "";

    public int IntervalMs { get; init; }

    public int ThumbnailWidth { get; init; }

    public int ThumbnailHeight { get; init; }

    public int TileColumns { get; init; }

    public int TileRows { get; init; }

    public int ThumbnailCount { get; init; }

    /// <summary>The set's own part of its sheets' names (<see cref="DownloadPaths.PreviewSheet"/>), new for every set kept.</summary>
    public string Generation { get; init => field = value ?? ""; } = "";

    /// <summary>How many sheets the set has; as many files are kept.</summary>
    public int Sheets { get; init; }

    /// <summary>What the sheets take on the disk.</summary>
    public long Bytes { get; init; }

    public bool TooLarge { get; init; }

    /// <summary>The set the server lists for <paramref name="revision"/>, before its sheets are kept.</summary>
    public static KeptPreviewsFile Of(TrickplayManifest server, long revision) => new()
    {
        Revision = revision,
        FileId = server.FileId,
        IntervalMs = server.IntervalMs,
        ThumbnailWidth = server.ThumbnailWidth,
        ThumbnailHeight = server.ThumbnailHeight,
        TileColumns = server.TileColumns,
        TileRows = server.TileRows,
        ThumbnailCount = server.ThumbnailCount,
        Sheets = server.Sheets.Count,
    };

    /// <summary>The same file, revision and layout as <paramref name="other"/>; what was kept of it may differ.</summary>
    public bool SameSetAs(KeptPreviewsFile other) =>
        this with { Bytes = 0, TooLarge = false, Generation = "" } == other with { Bytes = 0, TooLarge = false, Generation = "" };
}

/// <summary>Reads the seek-bar previews kept with a download, for a play without the server.</summary>
public static class KeptPreviews
{
    /// <summary>The most sheets a kept set has; a 2-hour film has a handful.</summary>
    public const int MaxSheets = 1000;

    /// <summary>
    /// The kept previews as a play shows them, their sheets the files in the folder; null when none are kept, or they do not fit
    /// the download: another revision's, another file's, a sheet missing from <paramref name="files"/>, or a file that does not
    /// read. Never throws: a download plays without its previews.
    /// </summary>
    public static PlayerTrickplay? Read(StoredDownload download, OfflineManifest manifest, IReadOnlySet<string> files, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(logger);
        if (!files.Contains(DownloadPaths.Previews))
        {
            return null;
        }
        string id = download.Record.DownloadId;
        try
        {
            if (ReadFile(download.Folder) is not { } kept)
            {
                return null;
            }
            string? fileId = manifest.MediaFileId ?? download.Record.MediaFileId;
            if (kept.TooLarge || !IsGeneration(kept.Generation) || kept.Revision != download.Record.Revision || fileId is null || kept.FileId != fileId || !IsDrawable(kept)
                || Enumerable.Range(0, kept.Sheets).Any(i => !files.Contains(DownloadPaths.PreviewSheet(kept.Generation, i))))
            {
                logger.LogInformation("The kept seek-bar previews of download {DownloadId} do not fit it; it plays without them", id);
                return null;
            }
            return PlayerTrickplay.Local(kept.FileId, kept.IntervalMs, kept.ThumbnailWidth, kept.ThumbnailHeight, kept.TileColumns, kept.TileRows,
                kept.ThumbnailCount,
                [.. Enumerable.Range(0, kept.Sheets).Select(i => Path.Combine(download.Folder, DownloadPaths.PreviewSheet(kept.Generation, i)))]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or UriFormatException)
        {
            // Only the kind: a file error's message holds the folder, with the Windows user's name.
            logger.LogInformation("The kept seek-bar previews of download {DownloadId} cannot be read ({Failure}); it plays without them", id,
                DownloadLog.Failure(ex));
            return null;
        }
    }

    /// <summary>
    /// A layout the seek bar can draw, of at least one and at most <see cref="MaxSheets"/> sheets, whose grid of tiles a sheet
    /// holds is counted in an <see cref="int"/> (<see cref="PlayerTrickplay.TileAt"/> divides by it).
    /// </summary>
    internal static bool IsDrawable(KeptPreviewsFile set) =>
        set is { IntervalMs: > 0, ThumbnailWidth: > 0, ThumbnailHeight: > 0, TileColumns: > 0, TileRows: > 0, ThumbnailCount: > 0, Sheets: > 0 }
        && set.Sheets <= MaxSheets && set.FileId.Length > 0 && (long)set.TileColumns * set.TileRows <= int.MaxValue;

    /// <summary>A generation a file name can carry: up to 32 letters and digits, as Granary makes them.</summary>
    internal static bool IsGeneration(string generation) =>
        generation.Length is > 0 and <= 32 && generation.All(char.IsAsciiLetterOrDigit);

    /// <summary>The kept set's description; null when there is none.</summary>
    /// <exception cref="JsonException">It does not read.</exception>
    internal static KeptPreviewsFile? ReadFile(string folder)
    {
        string path = Path.Combine(folder, DownloadPaths.Previews);
        return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), KeptPreviewsJsonContext.Default.KeptPreviewsFile) : null;
    }

    internal static byte[] Write(KeptPreviewsFile set) => JsonSerializer.SerializeToUtf8Bytes(set, KeptPreviewsJsonContext.Default.KeptPreviewsFile);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(KeptPreviewsFile))]
internal sealed partial class KeptPreviewsJsonContext : JsonSerializerContext;
