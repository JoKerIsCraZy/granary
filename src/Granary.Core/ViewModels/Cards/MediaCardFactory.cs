using System.Globalization;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;

namespace Granary.Core.ViewModels.Cards;

/// <summary>
/// Turns catalog items into <see cref="MediaCard"/>s worded like Silo's web client
/// (web/src/lib/overlays, web/src/lib/mediaFormat.ts): "1080P", "4K DV", "EAC3", "RETURNING", "IMDB 7.5".
/// </summary>
public static class MediaCardFactory
{
    /// <summary>Continue Watching and Next Up rows show backdrop cards with progress, every other row posters (docs/api-contract.md, 7.1).</summary>
    public static MediaCard ForSection(Section section, CatalogItemBase item, Uri serverBase) =>
        section.SectionType is SectionTypes.ContinueWatching or SectionTypes.NextUp
            ? Landscape(item, serverBase)
            : Poster(item, serverBase);

    public static MediaCard Poster(CatalogItemBase item, Uri serverBase)
    {
        bool isEpisode = item.Type == CatalogItemTypes.Episode;
        return Decorate(new MediaCard(item.ContentId, item.Type, isEpisode ? item.SeriesTitle ?? item.Title : item.Title)
        {
            Shape = MediaCardShape.Poster,
            Subtitle = PosterSubtitle(item),
            ImageUrl = ArtworkUrl.Resolve(serverBase, item.PosterUrl) ?? ArtworkUrl.Resolve(serverBase, item.BackdropUrl),
        }, item);
    }

    /// <summary>"ted" / "Season 1 Episode 1 • Schulanfang" / "18 min left", as on the web's Continue Watching row.</summary>
    public static MediaCard Landscape(CatalogItemBase item, Uri serverBase)
    {
        bool isEpisode = item.Type == CatalogItemTypes.Episode;
        return Decorate(new MediaCard(item.ContentId, item.Type, isEpisode ? item.SeriesTitle ?? item.Title : item.Title)
        {
            Shape = MediaCardShape.Landscape,
            Subtitle = isEpisode ? EpisodeLine(item.SeasonNumber, item.EpisodeNumber, item.Title) : YearText(item.Year),
            Caption = item.ItemSource == ItemSources.NextUp ? "Next episode" : TimeLeft(item.PositionSeconds, item.DurationSeconds),
            ImageUrl = ArtworkUrl.Resolve(serverBase, item.BackdropUrl) ?? ArtworkUrl.Resolve(serverBase, item.PosterUrl),
        }, item);
    }

    /// <summary>A season poster: "Staffel 3" with "4 of 8 episodes" and the season's progress.</summary>
    public static MediaCard Season(Season season, Uri serverBase)
    {
        int watched = season.UserData?.WatchedCount ?? 0;
        return new MediaCard(season.ContentId, CatalogItemTypes.Season, season.Title)
        {
            Shape = MediaCardShape.Poster,
            Caption = watched > 0 && watched < season.EpisodeCount
                ? $"{watched} of {season.EpisodeCount} episodes"
                : $"{season.EpisodeCount} {(season.EpisodeCount == 1 ? "episode" : "episodes")}",
            ImageUrl = ArtworkUrl.Resolve(serverBase, season.PosterUrl),
            Progress = season.EpisodeCount > 0 && watched > 0 && watched < season.EpisodeCount ? (double)watched / season.EpisodeCount : null,
            IsWatched = season.UserData?.Played == true,
            PlayContentId = season.PlayContentId,
        };
    }

    /// <summary>An episode still: "S03E01 · Chapter 1" with "23 Oct 2026 · 52m".</summary>
    public static MediaCard Episode(Episode episode, Uri serverBase)
    {
        var meta = new[] { FormatDate(episode.AirDate), episode.Runtime > 0 ? $"{episode.Runtime}m" : null }.Where(s => s is not null);
        return new MediaCard(episode.ContentId, CatalogItemTypes.Episode,
            $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00} · {episode.Title}")
        {
            Shape = MediaCardShape.Landscape,
            Subtitle = string.Join(" · ", meta) is { Length: > 0 } line ? line : null,
            Caption = episode.Overview,
            ImageUrl = ArtworkUrl.Resolve(serverBase, episode.StillUrl),
            ResolutionBadge = ResolutionText(episode.OverlaySummary),
            AudioBadge = AudioText(episode.OverlaySummary),
            Progress = ProgressOf(episode.UserData?.PositionSeconds, episode.UserData?.DurationSeconds, episode.UserData?.Played == true),
            IsWatched = episode.UserData?.Played == true,
            PlayContentId = episode.ContentId,
        };
    }

    /// <summary>"1080P", "4K DV", "1080P HDR" (web: prettyResolution + compactHdrSuffix, upper-cased like its badges).</summary>
    public static string? ResolutionText(CatalogItemOverlay? overlay)
    {
        string? resolution = overlay?.Resolution?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(resolution))
        {
            return null;
        }
        string pretty = resolution switch
        {
            "2160p" or "4k" or "uhd" => "4K",
            "4320p" or "8k" => "8K",
            _ => resolution,
        };
        string? hdr = overlay!.Hdr is { Length: > 0 } value ? (value.Contains("DV", StringComparison.Ordinal) ? "DV" : "HDR") : null;
        return (hdr is null ? pretty : $"{pretty} {hdr}").ToUpperInvariant();
    }

    public static string? AudioText(CatalogItemOverlay? overlay) =>
        overlay?.Audio is { Length: > 0 } audio ? audio.Trim().ToUpperInvariant() : null;

    /// <summary>The web's formatShowStatus, upper-cased.</summary>
    public static string? ShowStatusText(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "returning" or "returning series" or "continuing" or "in_production" or "in production" => "RETURNING",
        "ended" => "ENDED",
        "cancelled" or "canceled" => "CANCELLED",
        "upcoming" or "planned" => "UPCOMING",
        { } other => other.ToUpperInvariant(),
    };

    /// <summary>"IMDB 7.5": the source always goes with the score.</summary>
    public static string? RatingText(double? imdb) =>
        imdb is { } score ? "IMDB " + (Math.Round(score * 10) / 10).ToString("0.0", CultureInfo.InvariantCulture) : null;

    private static MediaCard Decorate(MediaCard card, CatalogItemBase item) => card with
    {
        ResolutionBadge = ResolutionText(item.OverlaySummary),
        AudioBadge = AudioText(item.OverlaySummary),
        StatusBadge = ShowStatusText(item.ShowStatus) ?? (item.Badges.Contains("new") ? "NEW" : null),
        RatingBadge = RatingText(item.RatingImdb),
        Progress = ProgressOf(item.PositionSeconds, item.DurationSeconds, item.UserState?.Played == true),
        IsWatched = item.UserState?.Played == true,
        PlayContentId = item.PlayContentId,
    };

    // Web: "2021 SERIES" under a series, the year under a movie, "S1 E5" under an episode.
    private static string? PosterSubtitle(CatalogItemBase item) => item.Type switch
    {
        CatalogItemTypes.Movie => YearText(item.Year),
        CatalogItemTypes.Series => item.Year is > 0 ? $"{item.Year} SERIES" : "SERIES",
        CatalogItemTypes.Season => item.SeasonNumber is { } season ? $"SEASON {season}" : null,
        CatalogItemTypes.Episode => item.SeasonNumber is { } s && item.EpisodeNumber is { } e ? $"S{s} E{e}" : null,
        _ => item.Year is > 0 ? $"{item.Year} {item.Type.ToUpperInvariant()}" : item.Type.ToUpperInvariant(),
    };

    private static string? EpisodeLine(int? season, int? episode, string title) =>
        season is { } s && episode is { } e ? $"Season {s} Episode {e} • {title}" : title;

    private static string? YearText(int? year) => year is > 0 ? year.Value.ToString(CultureInfo.InvariantCulture) : null;

    private static string? TimeLeft(double? position, double? duration)
    {
        if (position is not > 0 || duration is not > 0 || position >= duration)
        {
            return null;
        }
        int minutes = (int)Math.Ceiling((duration.Value - position.Value) / 60);
        return $"{minutes} min left";
    }

    private static double? ProgressOf(double? position, double? duration, bool played) =>
        !played && position is > 0 && duration is > 0 ? Math.Clamp(position.Value / duration.Value, 0, 1) : null;

    // Server dates are ISO strings ("2026-10-23"); shown like the web: "23 Oct 2026".
    private static string? FormatDate(string? isoDate) =>
        DateOnly.TryParse(isoDate?.Length >= 10 ? isoDate[..10] : isoDate, CultureInfo.InvariantCulture, out var date)
            ? date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
            : null;
}
