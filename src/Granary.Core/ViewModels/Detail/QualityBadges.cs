using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Player;

namespace Granary.Core.ViewModels.Detail;

/// <summary>
/// The accent badges after a movie's or episode's facts, as Silo's web detail page has them
/// (web/src/pages/ItemDetail/components/QualityBadges.tsx on selectedMediaSummary.ts): the chosen file's resolution, its
/// range ("DV HDR10", "HDR10+", none for SDR) and its best audio ("DD+ ATMOS"), in that order and in capitals.
/// </summary>
public static class QualityBadges
{
    // web: AUDIO_RANK in versionRankingUtils.ts, in its order: the first family a codec's name holds gives its rank.
    private static readonly (string Family, int Rank)[] AudioRanks =
    [
        ("atmos", 6), ("truehd", 5), ("dts-hd", 4), ("dts:x", 4), ("dts", 3), ("flac", 2), ("eac3", 1), ("e-ac-3", 1), ("aac", 0),
    ];

    /// <summary>
    /// The badges of <paramref name="selected"/>, the file the Version picker shows. None for a series or season, whose
    /// pages the web gives the metadata badges alone, and none without a file.
    /// </summary>
    public static IReadOnlyList<string> For(CatalogItemDetail item, FileVersion? selected)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (selected is null || item.Type is not (CatalogItemTypes.Movie or CatalogItemTypes.Episode))
        {
            return [];
        }
        string[] labels =
        [
            // Only a resolution the web ranks: its pickBestAttributes takes none below its lowest class.
            PrePlayText.ResolutionScore(selected.Resolution) >= 0 ? selected.Resolution : "",
            PlaybackInfo.VideoRangeLabel(selected.VideoTracks, selected.Hdr),
            BestAudio(selected),
        ];
        return [.. labels.Where(label => label.Length > 0).Select(label => label.ToUpperInvariant())];
    }

    // web: bestAttributes over the one file. The file's own codec first, then each track's: a higher family wins, and
    // among equals one that names Atmos, so an Atmos track that is not the first still gives the badge. A codec of no
    // ranked family (AC3, Opus) gives none.
    private static string BestAudio(FileVersion version)
    {
        string best = "";
        int bestRank = -1;
        Consider(version.CodecAudio, PrePlayText.AudioLabel(version));
        foreach (var track in version.AudioTracks)
        {
            if (!string.IsNullOrEmpty(track.Codec))
            {
                Consider(track.Codec, TrackText.AudioCodec(track));
            }
        }
        return best;

        void Consider(string codec, string label)
        {
            int rank = AudioRank(codec);
            if (rank > bestRank || (rank >= 0 && rank == bestRank && !NamesAtmos(best) && NamesAtmos(label)))
            {
                bestRank = rank;
                best = label;
            }
        }
    }

    private static int AudioRank(string codec)
    {
        string lower = codec.ToLowerInvariant();
        foreach (var (family, rank) in AudioRanks)
        {
            if (lower.Contains(family, StringComparison.Ordinal))
            {
                return rank;
            }
        }
        return -1;
    }

    private static bool NamesAtmos(string label) => label.Contains("Atmos", StringComparison.Ordinal);
}
