using System.Text.Json.Serialization;
using Granary.Core.Api;
using Granary.Core.Api.Downloads;

namespace Granary.Core.Downloads;

/// <summary>
/// <c>downloads.json</c> in the data folder: where each download's folder is, with enough of it to show its row while that
/// folder is missing, the server deletes still to be sent, and the folders of removed downloads still to be deleted.
/// </summary>
internal sealed record DownloadIndexFile
{
    public int Version { get; init; } = 1;

    public IReadOnlyList<DownloadIndexEntry> Downloads { get; init => field = value ?? []; } = [];

    public IReadOnlyList<PendingDelete> PendingDeletes { get; init => field = value ?? []; } = [];

    public IReadOnlyList<FolderDelete> FolderDeletes { get; init => field = value ?? []; } = [];
}

/// <summary>One download in the index: its scope, id and folder, and the row's description for a folder that is missing.</summary>
internal sealed record DownloadIndexEntry(
    DownloadScope Scope,
    string DownloadId,
    string Folder,
    string MediaItemId,
    string Title,
    string? SeriesId,
    string? SeriesTitle,
    int? SeasonNumber,
    int? EpisodeNumber)
{
    public static DownloadIndexEntry Of(StoredDownload download)
    {
        var record = download.Record;
        return new DownloadIndexEntry(record.Scope, record.DownloadId, download.Folder, record.MediaItemId, record.Title, record.SeriesId,
            record.SeriesTitle, record.SeasonNumber, record.EpisodeNumber);
    }

    /// <summary>Every member a row needs, and a folder laid out for this scope and id (JSON can say null for anything).</summary>
    public bool IsUsable =>
        DownloadRecords.IsUsable(Scope) && !string.IsNullOrWhiteSpace(DownloadId) && !string.IsNullOrWhiteSpace(MediaItemId) && Title is not null
        && DownloadRecords.IsOwnFolder(Folder, Scope, DownloadId);

    /// <summary>A row with what the index knows of it, until its folder is back.</summary>
    public DownloadRecord Placeholder() => new()
    {
        DownloadId = DownloadId,
        Scope = Scope,
        MediaItemId = MediaItemId,
        Title = Title,
        SeriesId = SeriesId,
        SeriesTitle = SeriesTitle,
        SeasonNumber = SeasonNumber,
        EpisodeNumber = EpisodeNumber,
        Quality = DownloadQualities.Original,
        State = DownloadState.Queued,
    };
}

/// <summary>The folder of a removed download, deleted once the index without it is written and nothing holds the folder.</summary>
internal sealed record FolderDelete(DownloadScope Scope, string DownloadId, string Folder)
{
    public bool IsUsable => DownloadRecords.IsUsable(Scope) && !string.IsNullOrWhiteSpace(DownloadId) && DownloadRecords.IsOwnFolder(Folder, Scope, DownloadId);
}

/// <summary>What the store takes from a file: JSON can say null or anything for every member, so each is checked.</summary>
internal static class DownloadRecords
{
    public static bool IsUsable(DownloadScope? scope) =>
        scope is not null && !string.IsNullOrWhiteSpace(scope.ServerEntryId) && !string.IsNullOrWhiteSpace(scope.AccountId)
        && !string.IsNullOrWhiteSpace(scope.ProfileId);

    public static bool IsUsable(PendingDelete? delete) => delete is not null && IsUsable(delete.Scope) && !string.IsNullOrWhiteSpace(delete.DownloadId);

    private static bool IsUsable(DownloadStatusEvent? report) => report is { Status: { Length: > 0 }, Revision: >= 0 };

    /// <summary>
    /// <paramref name="folder"/> is laid out as <see cref="DownloadPaths.Folder"/> lays out this scope's download: only such a folder
    /// is read, written or deleted, whatever a file says.
    /// </summary>
    public static bool IsOwnFolder(string? folder, DownloadScope scope, string downloadId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            {
                return false;
            }
            string? root = Path.GetFullPath(folder);
            for (int up = 0; up < 4 && root is not null; up++)
            {
                root = Path.GetDirectoryName(root);
            }
            return root is not null && string.Equals(DownloadPaths.Folder(root, scope, downloadId), folder, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The record as it can be used, or null when a member it needs is missing or impossible: a damaged record. A validator
    /// <c>If-Range</c> does not take, or a media or artwork file name the store does not make, is dropped (the download starts
    /// over, or is not played or shown from that name).
    /// </summary>
    public static DownloadRecord? Clean(DownloadRecord? record)
    {
        if (record is null || !IsUsable(record.Scope) || string.IsNullOrWhiteSpace(record.DownloadId) || string.IsNullOrWhiteSpace(record.MediaItemId)
            || record.Title is null || string.IsNullOrWhiteSpace(record.Quality) || record.ExpectedBytes < 0 || record.Revision < 0
            || !Enum.IsDefined(record.State) || !Enum.IsDefined(record.Failure))
        {
            return null;
        }
        var pending = IsUsable(record.PendingReport) ? record.PendingReport : null;
        var next = IsUsable(record.NextReport) ? record.NextReport : null;
        return record with
        {
            Validator = ByteRange.IsValidator(record.Validator) ? record.Validator : null,
            MediaFile = DownloadPaths.IsMediaName(record.MediaFile) ? record.MediaFile : null,
            PreparationProgress = record.PreparationProgress is >= 0 and <= 1 ? record.PreparationProgress : null,
            // The next report moves up when the one before it is gone.
            PendingReport = pending ?? next,
            NextReport = pending is null ? null : next,
            SubtitleRevisions = record.SubtitleRevisions.Where(r => r.Key is not null && r.Value is not null).ToDictionary(),
            ArtworkFiles = record.ArtworkFiles.Where(a => DownloadPaths.IsArtworkName(a.Key, a.Value)).ToDictionary(),
            SubtitlePreference = record.SubtitlePreference is { Mode.Length: > 0 } ? record.SubtitlePreference : null,
            DurationMs = record.DurationMs is > 0 ? record.DurationMs : null,
        };
    }

    /// <summary>A manifest's length in milliseconds; null when it says none, or one that cannot be.</summary>
    public static long? DurationOf(Api.Downloads.OfflineManifest manifest) =>
        manifest.DurationSeconds is { } seconds && double.IsFinite(seconds) && seconds > 0 && seconds < long.MaxValue / 1000.0
            ? (long)Math.Round(seconds * 1000)
            : null;
}

/// <summary>The index and each folder's <see cref="DownloadRecord"/>, as Granary writes them.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(DownloadIndexFile))]
[JsonSerializable(typeof(DownloadRecord))]
internal sealed partial class DownloadStoreJsonContext : JsonSerializerContext;
