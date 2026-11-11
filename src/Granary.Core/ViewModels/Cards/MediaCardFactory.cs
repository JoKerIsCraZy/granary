using System.Globalization;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;
using Granary.Core.Settings;

namespace Granary.Core.ViewModels.Cards;

/// <summary>
/// Turns catalog items into <see cref="MediaCard"/>s worded like Silo's web client
/// (web/src/lib/overlays, web/src/lib/mediaFormat.ts): "1080P", "4K DV", "EAC3", "RETURNING", "IMDB 7.5".
/// </summary>
public static class MediaCardFactory
{
    /// <summary>
    /// Continue Watching and Next Up rows show backdrop cards with progress, every other row posters (docs/api-contract.md,
    /// 7.1). Only the former blur an unwatched episode's still, as the web's <c>SectionRow</c>.
    /// </summary>
    public static MediaCard ForSection(Section section, CatalogItemBase item, Uri serverBase, EpisodeSpoilers spoilers = default)
    {
        ArgumentNullException.ThrowIfNull(section);
        return ForSection(section.SectionType, item, serverBase, spoilers);
    }

    /// <inheritdoc cref="ForSection(Section, CatalogItemBase, Uri, EpisodeSpoilers)"/>
    public static MediaCard ForSection(string? sectionType, CatalogItemBase item, Uri serverBase, EpisodeSpoilers spoilers = default) =>
        sectionType is SectionTypes.ContinueWatching or SectionTypes.NextUp
            ? Landscape(item, serverBase, spoilers)
            : Poster(item, serverBase);

    /// <summary>"Dune" / "2021"; an episode as the web's grid card: "Tracker" / "Safe Harbor" / "S04E01".</summary>
    public static MediaCard Poster(CatalogItemBase item, Uri serverBase)
    {
        var episode = EpisodeLabels(item);
        string title = episode?.Title ?? (item.Type == CatalogItemTypes.Episode ? item.SeriesTitle ?? item.Title : item.Title);
        return Decorate(new MediaCard(item.ContentId, item.Type, title)
        {
            Shape = MediaCardShape.Poster,
            EpisodeTitle = episode?.EpisodeTitle,
            Subtitle = episode?.Code ?? PosterSubtitle(item),
            ImageUrl = ArtworkUrl.Resolve(serverBase, item.PosterUrl) ?? ArtworkUrl.Resolve(serverBase, item.BackdropUrl),
            ImageThumbhash = ArtworkUrl.Resolve(serverBase, item.PosterUrl) is not null ? item.PosterThumbhash : item.BackdropThumbhash,
        }, item);
    }

    /// <summary>
    /// "ted" / "Season 1 Episode 1 • Schulanfang" / "18 min left", as on the web's Continue Watching row. The first episode of
    /// a season that has just begun says "NEW SEASON" after its time left, and in place of "Next Episode" on a Next Up card,
    /// as the web's <c>ContinueWatchingCard</c>.
    /// </summary>
    public static MediaCard Landscape(CatalogItemBase item, Uri serverBase, EpisodeSpoilers spoilers = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        bool isEpisode = item.Type == CatalogItemTypes.Episode;
        bool showsBackdrop = ArtworkUrl.Resolve(serverBase, item.BackdropUrl) is not null;
        bool premiere = item.Badges.Contains(ItemBadges.SeasonPremiere);
        return Decorate(new MediaCard(item.ContentId, item.Type, isEpisode ? item.SeriesTitle ?? item.Title : item.Title)
        {
            Shape = MediaCardShape.Landscape,
            Subtitle = isEpisode ? EpisodeLine(item.SeasonNumber, item.EpisodeNumber, item.Title) : YearText(item.Year),
            Caption = item.ItemSource == ItemSources.NextUp
                ? (premiere ? null : "Next Episode")
                : TimeLeft(item.PositionSeconds, item.DurationSeconds),
            CaptionBadge = premiere ? "NEW SEASON" : null,
            CaptionBadgeName = premiere ? "new season" : null,
            ImageUrl = ArtworkUrl.Resolve(serverBase, item.BackdropUrl) ?? ArtworkUrl.Resolve(serverBase, item.PosterUrl),
            ImageThumbhash = showsBackdrop ? item.BackdropThumbhash : item.PosterThumbhash,
            BlursImage = spoilers.HidesImage(item, showsBackdrop),
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
            ImageThumbhash = season.PosterThumbhash,
            // A season watched to the end keeps a full bar, green, as on the web.
            Progress = season.EpisodeCount > 0 && watched > 0 ? Math.Min(1, (double)watched / season.EpisodeCount) : null,
            IsWatched = season.UserData?.Played == true,
            ShowsWatchedMark = true,
            PlayContentId = season.PlayContentId,
        };
    }

    /// <summary>
    /// An episode still: "S03E01 · Chapter 1" with "23 Oct 2026 · 52m" and the overview; an unwatched one's still blurred and
    /// its overview left out as the profile's <paramref name="spoilers"/> say, as the web's <c>EpisodeCarousel</c>.
    /// </summary>
    public static MediaCard Episode(Episode episode, Uri serverBase, EpisodeSpoilers spoilers = default)
    {
        ArgumentNullException.ThrowIfNull(episode);
        var meta = new[] { FormatDate(episode.AirDate), episode.Runtime > 0 ? $"{episode.Runtime}m" : null }.Where(s => s is not null);
        return new MediaCard(episode.ContentId, CatalogItemTypes.Episode,
            $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00} · {episode.Title}")
        {
            Shape = MediaCardShape.Landscape,
            Subtitle = string.Join(" · ", meta) is { Length: > 0 } line ? line : null,
            Caption = spoilers.HidesOverview(episode) ? null : episode.Overview,
            ImageUrl = ArtworkUrl.Resolve(serverBase, episode.StillUrl),
            ImageThumbhash = episode.StillThumbhash,
            BlursImage = spoilers.HidesImage(episode),
            Overlay = OverlayOf(episode.OverlaySummary) with { Runtime = episode.Runtime > 0 ? episode.Runtime : null },
            Progress = ProgressOf(episode.UserData?.PositionSeconds, episode.UserData?.DurationSeconds, episode.UserData?.Played == true),
            IsWatched = episode.UserData?.Played == true,
            ShowsWatchedMark = true,
            PlayContentId = episode.ContentId,
        };
    }

    /// <summary>The <see cref="MediaCard.Type"/> of a box set's card, which opens the box set's page.</summary>
    public const string BoxSetType = "box_set";

    /// <summary>
    /// A box set's poster as the web's <c>BoxSetCard</c>: its name over "1 / 3 WATCHED" once one is watched, else its years,
    /// else its films; the films in the corner and the watched share as its bar. Search can find one collection in two
    /// libraries, so the card's id names the library too. It offers no play disc, buttons or menu: none of them fit a set.
    /// </summary>
    public static MediaCard BoxSet(CatalogBoxSetBase boxSet, Uri serverBase)
    {
        ArgumentNullException.ThrowIfNull(boxSet);
        var poster = ArtworkUrl.Resolve(serverBase, boxSet.PosterUrl);
        string caption = boxSet.PlayedCount > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{boxSet.PlayedCount} / {boxSet.MovieCount} watched")
            : BoxSetText.Years(boxSet) ?? BoxSetText.FilmCount(boxSet.MovieCount);
        return new MediaCard($"{BoxSetType}:{boxSet.LibraryId}:{boxSet.Id}", BoxSetType, boxSet.Title)
        {
            Shape = MediaCardShape.Poster,
            Subtitle = caption.ToUpperInvariant(),
            ImageUrl = poster ?? ArtworkUrl.Resolve(serverBase, boxSet.BackdropUrl),
            ImageThumbhash = poster is not null ? boxSet.PosterThumbhash : boxSet.BackdropThumbhash,
            Progress = boxSet.PlayedCount > 0 && boxSet.MovieCount > 0 ? Math.Min(1, (double)boxSet.PlayedCount / boxSet.MovieCount) : null,
            BoxSetFilms = BoxSetText.FilmCount(boxSet.MovieCount),
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

    /// <summary>What a title's badges can say, as the web's overlay extractor (<c>lib/overlays/extractors.ts</c>) takes it from a card.</summary>
    public static Overlays.OverlayData OverlayOf(CatalogItemBase item) => OverlayOf(item.OverlaySummary) with
    {
        RatingImdb = item.RatingImdb,
        RatingTmdb = item.RatingTmdb,
        RatingRtCritic = item.RatingRtCritic,
        RatingRtAudience = item.RatingRtAudience,
        ContentRating = string.IsNullOrEmpty(item.ContentRating) ? null : item.ContentRating,
        AdvisoryAge = item.AdvisoryAge,
        Year = item.Year is > 0 ? item.Year : null,
        Runtime = item.Runtime,
        OriginalLanguage = item.OriginalLanguage,
        Studio = item.Studios.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim(),
        Network = item.Networks.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim(),
        ShowStatus = string.IsNullOrEmpty(item.ShowStatus) ? null : item.ShowStatus,
    };

    private static Overlays.OverlayData OverlayOf(CatalogItemOverlay? summary) => new()
    {
        Resolution = summary?.Resolution,
        Hdr = summary?.Hdr,
        Audio = summary?.Audio,
        AudioChannels = summary?.AudioChannels,
        VideoCodec = summary?.VideoCodec,
        Container = summary?.Container,
        AspectRatio = summary?.AspectRatio,
        ReleaseType = summary?.ReleaseType,
        Edition = summary?.Edition,
        MultiAudio = summary?.MultiAudio == true,
        MultiSub = summary?.MultiSub == true,
    };

    private static MediaCard Decorate(MediaCard card, CatalogItemBase item) => card with
    {
        Overlay = OverlayOf(item),
        StatusBadge = item.Badges.Contains(ItemBadges.New) ? "NEW" : null,
        Progress = ProgressOf(item.PositionSeconds, item.DurationSeconds, item.UserState?.Played == true),
        // Like the web, rows and grids mark no watched titles (season and episode cards do); the state shows on hover.
        IsWatched = item.UserState?.Played == true,
        IsFavorite = item.UserState?.IsFavorite == true,
        InWatchlist = item.UserState?.InWatchlist == true,
        IsNotInterested = item.UserState?.NotInterested == true,
        PlayContentId = item.PlayContentId,
    };

    // Web: "2021 SERIES" under a series, the year under a movie, "S1 E5" under an episode.
    private static string? PosterSubtitle(CatalogItemBase item) => item.Type switch
    {
        CatalogItemTypes.Movie => YearText(item.Year),
        CatalogItemTypes.Series => item.Year is > 0 ? $"{item.Year} SERIES" : "SERIES",
        CatalogItemTypes.Season => item.SeasonNumber is { } season ? $"SEASON {season}" : null,
        CatalogItemTypes.Episode => YearText(item.Year),
        _ => item.Year is > 0 ? $"{item.Year} {item.Type.ToUpperInvariant()}" : item.Type.ToUpperInvariant(),
    };

    // The web's episode card labels (lib/episodeCardLabels.ts): the series' title, the episode's own when it differs, and
    // "S04E01" zero-padded as the apps write it; none without the numbers or a title.
    private static (string Title, string? EpisodeTitle, string Code)? EpisodeLabels(CatalogItemBase item)
    {
        if (item.Type != CatalogItemTypes.Episode || item.SeasonNumber is not { } season || item.EpisodeNumber is not { } episode
            || string.IsNullOrEmpty(item.Title))
        {
            return null;
        }
        bool hasSeriesTitle = !string.IsNullOrEmpty(item.SeriesTitle) && item.SeriesTitle != item.Title;
        return (hasSeriesTitle ? item.SeriesTitle! : item.Title, hasSeriesTitle ? item.Title : null,
            string.Create(CultureInfo.InvariantCulture, $"S{season:00}E{episode:00}"));
    }

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
        DateOnly.TryParse(isoDate?.Length >= 10 ? isoDate[..10] : isoDate, CultureInfo.InvariantCulture, out var date) ? FormatDate(date) : null;

    /// <summary>An episode card's date: "23 Oct 2026".</summary>
    internal static string FormatDate(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}
