using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Cards;

namespace Granary.Core.ViewModels.Detail;

/// <summary>
/// The hero's texts per item type, worded like Silo's web client (web/src/pages/ItemDetail/*Content.tsx):
/// overline, title, chips, ratings, versions, breadcrumbs and the labels of the primary actions.
/// </summary>
public static class DetailFacts
{
    public const int MaximumRatings = 3;

    public static bool IsVideo(string type) =>
        type is CatalogItemTypes.Movie or CatalogItemTypes.Series or CatalogItemTypes.Season or CatalogItemTypes.Episode;

    /// <summary>Season 0 holds the specials.</summary>
    public static bool IsSpecials(Season season)
    {
        ArgumentNullException.ThrowIfNull(season);
        return season.IsSpecials == true || season.SeasonNumber == 0;
    }

    /// <summary>"MOVIE · AMAZON MGM STUDIOS", "SERIES · NETFLIX"; null for seasons and episodes, which show breadcrumbs.</summary>
    public static string? Overline(CatalogItemDetail item)
    {
        ArgumentNullException.ThrowIfNull(item);
        string? label = item.Type switch
        {
            CatalogItemTypes.Season or CatalogItemTypes.Episode => null,
            CatalogItemTypes.Movie => With("MOVIE", item.Studios),
            CatalogItemTypes.Series => With("SERIES", item.Networks),
            { Length: > 0 } other => other.Replace('_', ' ').ToUpperInvariant(),
            _ => null,
        };
        return label;

        static string With(string type, IReadOnlyList<string> companies) =>
            companies.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) is { } company ? $"{type} · {company.Trim().ToUpperInvariant()}" : type;
    }

    /// <summary>The title the hero shows: a season shows its series, an episode "S03E01 · Chapter 1".</summary>
    public static string DisplayTitle(CatalogItemDetail item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Type switch
        {
            CatalogItemTypes.Season => string.IsNullOrWhiteSpace(item.SeriesTitle) ? item.Title : item.SeriesTitle,
            CatalogItemTypes.Episode when item.SeasonNumber is { } season && item.EpisodeNumber is { } episode =>
                DetailText.EpisodeTitle(season, episode, item.Title),
            _ => item.Title,
        };
    }

    /// <summary>A season's own name under the series title ("Staffel 3") when it says more than "Season 3".</summary>
    public static string? Subtitle(CatalogItemDetail item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Type != CatalogItemTypes.Season || string.IsNullOrWhiteSpace(item.Title))
        {
            return null;
        }
        string title = item.Title.Trim();
        return title == DetailText.SeasonLabel(item.SeasonNumber ?? 0) ? null : title;
    }

    /// <summary>
    /// Movie: year, rating, runtime, then resolution and audio. Series: air years, rating, seasons and
    /// episodes (from the season list once it is there). Season: year, "SEASON 3", episodes. Episode: runtime,
    /// air date, resolution and audio. All upper-cased like the web's chips.
    /// </summary>
    public static IReadOnlyList<DetailChip> Chips(CatalogItemDetail item, IReadOnlyList<Season>? seasons = null, int? listedEpisodes = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var chips = new List<DetailChip>();
        switch (item.Type)
        {
            case CatalogItemTypes.Series:
                Add(DetailText.AirYears(item.FirstAirDate, item.LastAirDate, item.Year));
                Add(item.ContentRating);
                // Counted like the server's season_count: regular seasons, without the specials.
                var regular = seasons?.Where(s => !IsSpecials(s)).ToList();
                int? seasonCount = regular is { Count: > 0 } ? regular.Count : item.SeasonCount;
                int? episodeCount = regular is { Count: > 0 } ? regular.Sum(s => s.EpisodeCount) : item.EpisodeCount;
                AddCount(seasonCount, "season", "seasons");
                AddCount(episodeCount, "episode", "episodes");
                break;
            case CatalogItemTypes.Season:
                Add(DetailText.YearOf(item.AirDate) ?? DetailText.YearOf(item.ReleaseDate) ?? DetailText.Year(item.Year));
                Add(DetailText.SeasonLabel(item.SeasonNumber ?? 0));
                AddCount(item.EpisodeCount ?? listedEpisodes, "episode", "episodes");
                break;
            case CatalogItemTypes.Episode:
                Add(DetailText.Runtime(RuntimeMinutes(item)));
                Add(DetailText.Date(item.AirDate ?? item.ReleaseDate));
                AddQuality();
                break;
            default:
                Add(DetailText.Year(item.Year) ?? DetailText.YearOf(item.ReleaseDate));
                Add(item.ContentRating);
                Add(DetailText.Runtime(RuntimeMinutes(item)));
                AddQuality();
                break;
        }
        return chips;

        void Add(string? text, bool isAccent = false)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                chips.Add(new DetailChip(text.Trim().ToUpperInvariant(), isAccent));
            }
        }

        void AddCount(int? count, string singular, string plural)
        {
            if (count is > 0 and var n)
            {
                Add(DetailText.Count(n, singular, plural));
            }
        }

        void AddQuality()
        {
            var version = PlayedVersion(item);
            var overlay = item.OverlaySummary ?? (version is null ? null : OverlayOf(version));
            Add(MediaCardFactory.ResolutionText(overlay), isAccent: true);
            Add(MediaCardFactory.AudioText(overlay) ?? version?.CodecAudio, isAccent: true);
        }
    }

    /// <summary>The runtime in minutes: the played (or first) file's duration, else the metadata runtime (web: selectedMediaSummary).</summary>
    public static int? RuntimeMinutes(CatalogItemDetail item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return PlayedVersion(item) is { Duration: > 0 } version ? (int)Math.Round(version.Duration / 60.0, MidpointRounding.AwayFromZero) : item.Runtime;
    }

    /// <summary>The ratings as the server gives them, at most <see cref="MaximumRatings"/>.</summary>
    public static IReadOnlyList<DetailRating> Ratings(CatalogItemDetail item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return [.. item.Ratings
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Display) && !(string.IsNullOrWhiteSpace(r.Name) && string.IsNullOrWhiteSpace(r.Source)))
            .Take(MaximumRatings)
            .Select(r => new DetailRating(string.IsNullOrWhiteSpace(r.Name) ? r.Source.Trim().ToUpperInvariant() : r.Name.Trim(), r.Display.Trim()))];
    }

    /// <summary>
    /// One short line per file when there are several: "DIRECTOR'S CUT · 4K HDR · HEVC · TRUEHD". File names
    /// and paths are left out: they are administrator data.
    /// </summary>
    public static IReadOnlyList<string> VersionLines(CatalogItemDetail item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Versions.Count < 2)
        {
            return [];
        }
        return [.. item.Versions.Select(v => string.Join(" · ", new[]
        {
            v.EditionRaw?.Trim().ToUpperInvariant(),
            MediaCardFactory.ResolutionText(OverlayOf(v)),
            v.CodecVideo.Trim().ToUpperInvariant(),
            v.CodecAudio.Trim().ToUpperInvariant(),
        }.Where(part => !string.IsNullOrEmpty(part)))).Where(line => line.Length > 0)];
    }

    /// <summary>
    /// Season: series › "Season 3". Episode: series › season › "Episode 1", where the season links once
    /// <paramref name="season"/> (from the series' season list) is known.
    /// </summary>
    public static IReadOnlyList<DetailCrumb> Breadcrumbs(CatalogItemDetail item, Season? season = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Type is not (CatalogItemTypes.Season or CatalogItemTypes.Episode))
        {
            return [];
        }
        string seriesTitle = string.IsNullOrWhiteSpace(item.SeriesTitle) ? "Series" : item.SeriesTitle.Trim();
        var crumbs = new List<DetailCrumb> { new(seriesTitle, Blank(item.SeriesId), IsFirst: true) };
        if (item.Type == CatalogItemTypes.Season)
        {
            crumbs.Add(new DetailCrumb(DetailText.SeasonLabel(item.SeasonNumber ?? 0), null, IsFirst: false));
            return crumbs;
        }
        if (item.SeasonNumber is { } number)
        {
            string label = season is null
                ? DetailText.SeasonLabel(number)
                : DetailText.SeasonDisplayTitle(season.SeasonNumber, season.Title, season.IsSpecials == true);
            crumbs.Add(new DetailCrumb(label, Blank(season?.ContentId), IsFirst: false));
        }
        if (item.EpisodeNumber is { } episode)
        {
            crumbs.Add(new DetailCrumb($"Episode {episode}", null, IsFirst: false));
        }
        return crumbs;
    }

    public const string ResumeLabel = "Resume";

    /// <summary>
    /// The primary button. A movie or episode resumes when it has a resume point. A series or season resumes
    /// an episode in progress, plays the next one when partly watched, and otherwise starts at the beginning
    /// (web: resolveLeafPrimaryAction, resolveSeriesPrimaryAction: "Start From Episode 1", or "Browse Series"
    /// with nothing to play; the season's "Play First Episode").
    /// </summary>
    public static DetailPlayAction PlayAction(CatalogItemDetail item, WatchRollup? watch, string? firstEpisodeId = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Type is CatalogItemTypes.Series or CatalogItemTypes.Season)
        {
            string? next = Blank(item.PlayContentId);
            if (next is not null && (watch is { InProgressCount: > 0 } || watch?.IsInProgress == true))
            {
                return new DetailPlayAction(ResumeLabel, next);
            }
            if (next is not null && watch is { WatchedCount: > 0, Played: false })
            {
                return new DetailPlayAction("Play Next", next);
            }
            if (item.Type == CatalogItemTypes.Season)
            {
                return new DetailPlayAction("Play First Episode", Blank(firstEpisodeId) ?? next);
            }
            return next is null ? new DetailPlayAction("Browse Series", null) : new DetailPlayAction("Start From Episode 1", next);
        }
        // Watched items store position 0, so any position is a live resume point, a rewatch included.
        bool resume = watch?.IsInProgress == true || watch?.PositionSeconds > 0;
        return new DetailPlayAction(resume ? ResumeLabel : "Play", Blank(item.PlayContentId) ?? item.ContentId);
    }

    /// <summary>The web's getWatchedActionLabel.</summary>
    public static string WatchedLabel(string type, bool isWatched) => type switch
    {
        CatalogItemTypes.Series => isWatched ? "Mark Series Unwatched" : "Mark Series Watched",
        CatalogItemTypes.Season => isWatched ? "Mark Season Unwatched" : "Mark Season Watched",
        _ => isWatched ? "Mark Unwatched" : "Mark Watched",
    };

    // The file the viewer played last, else the first one.
    private static FileVersion? PlayedVersion(CatalogItemDetail item) =>
        item.Versions.FirstOrDefault(v => v.FileId.Length > 0 && v.FileId == item.UserData?.LastFileId) ?? item.Versions.FirstOrDefault();

    private static CatalogItemOverlay OverlayOf(FileVersion version) =>
        new() { Resolution = version.Resolution, Hdr = version.Hdr ? "HDR" : null };

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
