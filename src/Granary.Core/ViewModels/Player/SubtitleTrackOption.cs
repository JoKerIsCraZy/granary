using Granary.Core.Api.Playback;

namespace Granary.Core.ViewModels.Player;

/// <summary>
/// One row of the player's subtitle menu, as Silo's web player draws it (SubtitleMenu): Off first, then each track by
/// its language, with chips for its format and where it comes from, and its own label under it when that says more.
/// </summary>
/// <param name="TrackId">The inventory <c>track_id</c> a pick passes on; null for Off.</param>
public sealed record SubtitleTrackOption(string? TrackId, string Title, string Format, string Source, string Detail, bool IsSelected)
{
    /// <summary>For screen readers: the row as one sentence.</summary>
    public string AccessibleName => string.Join(", ", new[] { Title, Format, Source, Detail }.Where(part => part.Length > 0));

    /// <summary>
    /// Off, then <paramref name="tracks"/> embedded first, external next, downloaded last (each in inventory order),
    /// with the row of <paramref name="selected"/> checked, or Off without one.
    /// </summary>
    public static IReadOnlyList<SubtitleTrackOption> For(IReadOnlyList<SubtitleInventoryItem> tracks, string? selected)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var rows = new List<SubtitleTrackOption>(tracks.Count + 1) { new(null, "Off", "", "", "", selected is null) };
        rows.AddRange(tracks
            .OrderBy(track => track.Source switch { SubtitleSources.External => 1, SubtitleSources.Downloaded => 2, _ => 0 })
            .Select(track =>
            {
                string language = TrackText.SubtitleLanguage(track.Language);
                return new SubtitleTrackOption(track.TrackId, language, TrackText.SubtitleFormat(track.Codec), TrackText.SubtitleSource(track.Source),
                    TrackText.SubtitleDetail(track.Label, track.Language, language, track.Codec), track.TrackId == selected);
            }));
        return rows;
    }
}
