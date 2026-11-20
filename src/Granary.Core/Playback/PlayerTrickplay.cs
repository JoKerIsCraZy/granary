using Granary.Core.Api.Images;
using Granary.Core.Api.Playback;

namespace Granary.Core.Playback;

/// <summary>
/// Seek-bar previews of the playing file (docs/api-contract.md, 11.4): sprite sheets with one thumbnail per interval
/// of media time. Thumbnail i shows what plays from i·interval on and sits on sheet i / (columns·rows), left to right
/// and top to bottom; every sheet has the full grid.
/// </summary>
/// <param name="Sheets">One address per sheet, in order; each carries its own authorization.</param>
/// <param name="ExpiresAt">When the sheet addresses stop working.</param>
public sealed record PlayerTrickplay(
    string FileId, int IntervalMs, int Width, int Height, int Columns, int Rows, int Count, IReadOnlyList<Uri> Sheets, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// For previews kept with a download: each sheet's file on this PC, in the order of <see cref="Sheets"/>, which then only
    /// name them. The sheets are read from these paths, never through their addresses; null for the server's.
    /// </summary>
    public IReadOnlyList<string>? SheetFiles { get; init; }

    /// <summary>Previews whose sheets are the files at <paramref name="files"/>, kept on this PC; they do not expire.</summary>
    public static PlayerTrickplay Local(string fileId, int intervalMs, int width, int height, int columns, int rows, int count,
        IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return new PlayerTrickplay(fileId, intervalMs, width, height, columns, rows, count, [.. files.Select(f => new Uri(f))], DateTimeOffset.MaxValue)
        {
            SheetFiles = files,
        };
    }

    /// <summary>The previews of <paramref name="manifest"/>, the sheets resolved against <paramref name="server"/>; null when they cannot be shown.</summary>
    public static PlayerTrickplay? Of(TrickplayManifest manifest, Uri server)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var sheets = manifest.Sheets.OrderBy(s => s.Index).Select(s => ArtworkUrl.Resolve(server, s.Url)).ToList();
        if (manifest.IntervalMs <= 0 || !IsDrawableGrid(manifest.ThumbnailWidth, manifest.ThumbnailHeight, manifest.TileColumns, manifest.TileRows)
            || manifest.ThumbnailCount <= 0 || sheets.Count == 0 || sheets.Any(s => s is null))
        {
            return null;
        }
        return new PlayerTrickplay(manifest.FileId, manifest.IntervalMs, manifest.ThumbnailWidth, manifest.ThumbnailHeight,
            manifest.TileColumns, manifest.TileRows, manifest.ThumbnailCount, [.. sheets.OfType<Uri>()], manifest.ExpiresAt);
    }

    /// <summary>The most thumbnails a sheet's row or column holds; the server's sheets have 10.</summary>
    public const int MaxTiles = 100;

    /// <summary>The widest and tallest thumbnail, in pixels; the server's are 320 wide.</summary>
    public const int MaxThumbnailSide = 4096;

    /// <summary>
    /// A grid the seek bar can draw: each thumbnail at most <see cref="MaxThumbnailSide"/> on a side, each sheet at most
    /// <see cref="MaxTiles"/> by <see cref="MaxTiles"/>. Within these, a thumbnail's offset and the width a sheet is decoded at
    /// stay sane numbers.
    /// </summary>
    public static bool IsDrawableGrid(int width, int height, int columns, int rows) =>
        width is > 0 and <= MaxThumbnailSide && height is > 0 and <= MaxThumbnailSide && columns is > 0 and <= MaxTiles && rows is > 0 and <= MaxTiles;

    /// <summary>The thumbnail for <paramref name="seconds"/> of media time; null outside the previews.</summary>
    public TrickplayTile? TileAt(double seconds)
    {
        if (!double.IsFinite(seconds))
        {
            return null;
        }
        int index = (int)Math.Clamp(Math.Floor(seconds * 1000 / IntervalMs), 0, Count - 1);
        int perSheet = Columns * Rows;
        int sheet = index / perSheet;
        if (sheet >= Sheets.Count)
        {
            return null;
        }
        int cell = index % perSheet;
        return new TrickplayTile(sheet, Sheets[sheet], cell % Columns * Width, cell / Columns * Height);
    }
}

/// <summary>A thumbnail's place: its sheet, and its offset there in the sheet's own pixels.</summary>
public readonly record struct TrickplayTile(int Sheet, Uri Url, int X, int Y);
