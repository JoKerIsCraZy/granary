using System.Globalization;
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

    // How many genres the meta line names; the Info dialog lists them all.
    private const int MetaGenres = 3;
    private const int InfoCast = 25;
    private const double Gigabyte = 1024d * 1024 * 1024;
    private const double Megabyte = 1024d * 1024;

    /// <summary>
    /// The one line under the title (as Silage's title page): a movie's "2026 · 1h 58m · R · Mystery, Thriller", a
    /// series' air years, rating, regular seasons and genres, a season's year and episodes, an episode's
    /// "S3 E1 · 47m · 8 Jan 2021 · TV-MA". Unknown parts are left out.
    /// </summary>
    public static string MetaLine(CatalogItemDetail item, IReadOnlyList<Season>? seasons = null, int? listedEpisodes = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        string? genres = item.Genres.Count > 0 ? string.Join(", ", item.Genres.Where(g => !string.IsNullOrWhiteSpace(g)).Take(MetaGenres)) : null;
        string?[] parts = item.Type switch
        {
            CatalogItemTypes.Series =>
            [
                DetailText.AirYears(item.FirstAirDate, item.LastAirDate, item.Year),
                item.ContentRating,
                Counted(seasons?.Count(s => !IsSpecials(s)) is > 0 and var regular ? regular : item.SeasonCount, "season", "seasons"),
                genres,
            ],
            CatalogItemTypes.Season =>
            [
                DetailText.YearOf(item.AirDate) ?? DetailText.YearOf(item.ReleaseDate) ?? DetailText.Year(item.Year),
                Counted(item.EpisodeCount ?? listedEpisodes, "episode", "episodes"),
            ],
            CatalogItemTypes.Episode =>
            [
                item.SeasonNumber is { } season && item.EpisodeNumber is { } episode
                    ? string.Create(CultureInfo.InvariantCulture, $"S{season} E{episode}")
                    : null,
                DetailText.Runtime(RuntimeMinutes(item)),
                DetailText.Date(item.AirDate ?? item.ReleaseDate),
                item.ContentRating,
            ],
            _ =>
            [
                DetailText.Year(item.Year) ?? DetailText.YearOf(item.ReleaseDate),
                DetailText.Runtime(RuntimeMinutes(item)),
                item.ContentRating,
                genres,
            ],
        };
        return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
    }

    /// <summary>
    /// The small line under the buttons: "Available version: 720P · H264 · EAC3 · 9.3 GB" for one file, the qualities of
    /// several ("Available versions: 4K HDR · 1080P"), null for a series or season. Never the file's name or path.
    /// </summary>
    public static string? AvailableVersionLine(CatalogItemDetail item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Versions.Count == 0)
        {
            return null;
        }
        if (item.Versions.Count > 1)
        {
            var qualities = item.Versions.Select(v => MediaCardFactory.ResolutionText(OverlayOf(v))).OfType<string>().Distinct().ToList();
            return qualities.Count > 0 ? "Available versions: " + string.Join(" · ", qualities) : null;
        }
        var version = item.Versions[0];
        string line = string.Join(" · ", new[]
        {
            MediaCardFactory.ResolutionText(OverlayOf(version)),
            Upper(version.CodecVideo),
            Upper(version.CodecAudio),
            SizeText(version.FileSize),
        }.Where(p => !string.IsNullOrEmpty(p)));
        return line.Length > 0 ? "Available version: " + line : null;
    }

    /// <summary>The primary button's words: a movie or episode says where it resumes ("Resume from 1:02:33"), else the action's own.</summary>
    public static string PlayButtonText(CatalogItemDetail item, WatchRollup? watch, DetailPlayAction action)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(action);
        bool leaf = item.Type is not (CatalogItemTypes.Series or CatalogItemTypes.Season);
        return leaf && action.Label == ResumeLabel && watch?.PositionSeconds is > 0 and var position
            ? "Resume from " + Clock(position)
            : action.Label;
    }

    /// <summary>
    /// The Info dialog: the details (with the full overview), the cast, the crew by job, and each file's facts. File
    /// names and paths are left out: they are administrator data. Sections without rows are left out.
    /// </summary>
    public static IReadOnlyList<DetailInfoSection> InfoSections(CatalogItemDetail item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var sections = new List<DetailInfoSection>();
        Add("Details",
        [
            Row("Title", DisplayTitle(item)),
            Row("Original title", item.OriginalTitle is { } original && original.Trim() != item.Title.Trim() ? original : null),
            Row(item.Type == CatalogItemTypes.Series ? "Aired" : "Released", item.Type switch
            {
                CatalogItemTypes.Series => DetailText.AirYears(item.FirstAirDate, item.LastAirDate, item.Year),
                CatalogItemTypes.Movie => DetailText.Date(item.ReleaseDate) ?? DetailText.Year(item.Year),
                _ => DetailText.Date(item.AirDate ?? item.ReleaseDate),
            }),
            Row("Runtime", DetailText.Runtime(RuntimeMinutes(item))),
            Row("Rating", item.ContentRating),
            Row("Genres", Joined(item.Genres)),
            Row("Countries", Joined(item.Countries)),
            Row("Studios", Joined(item.Studios)),
            Row("Networks", Joined(item.Networks)),
            Row("Ratings", Ratings(item) is { Count: > 0 } ratings ? string.Join(" · ", ratings.Select(r => r.AccessibleName)) : null),
            Row("Tagline", item.Tagline),
            Row("Overview", item.Overview),
        ]);
        Add("Cast", [.. item.Cast
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Name))
            .OrderBy(c => c.Order)
            .Take(InfoCast)
            .Select(c => Row(c.Name, string.IsNullOrWhiteSpace(c.Character) ? "—" : c.Character))]);
        Add("Crew", [.. item.Crew
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Job))
            .GroupBy(c => c.Job.Trim(), StringComparer.Ordinal)
            .Select(g => Row(g.Key, string.Join(", ", g.Select(c => c.Name.Trim()).Distinct(StringComparer.Ordinal))))]);
        for (int i = 0; i < item.Versions.Count; i++)
        {
            var version = item.Versions[i];
            Add(item.Versions.Count == 1 ? "File" : string.Create(CultureInfo.InvariantCulture, $"File {i + 1}"),
            [
                Row("Edition", version.EditionRaw),
                Row("Resolution", MediaCardFactory.ResolutionText(OverlayOf(version))),
                Row("Video", Upper(version.CodecVideo)),
                Row("Audio", Upper(version.CodecAudio)),
                Row("Container", Upper(version.Container)),
                Row("Size", SizeText(version.FileSize)),
                Row("Bitrate", version.Bitrate > 0 ? string.Create(CultureInfo.InvariantCulture, $"{version.Bitrate / 1000d:0.#} Mb/s") : null),
                Row("Duration", version.Duration > 0 ? DetailText.Runtime((int)Math.Round(version.Duration / 60d, MidpointRounding.AwayFromZero)) : null),
            ]);
        }
        return sections;

        void Add(string title, DetailInfoRow?[] rows)
        {
            List<DetailInfoRow> kept = [.. rows.OfType<DetailInfoRow>()];
            if (kept.Count > 0)
            {
                sections.Add(new DetailInfoSection(title, kept));
            }
        }

        static DetailInfoRow? Row(string label, string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : new DetailInfoRow(label.Trim(), value.Trim());

        static string? Joined(IReadOnlyList<string> values) =>
            values.Any(v => !string.IsNullOrWhiteSpace(v)) ? string.Join(", ", values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())) : null;
    }

    private static string? Counted(int? count, string singular, string plural) =>
        count is > 0 and var n ? DetailText.Count(n, singular, plural) : null;

    private static string? Upper(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim().ToUpperInvariant();

    // "9.3 GB", "700 MB" (binary units, as file managers count them).
    private static string? SizeText(long bytes) => bytes switch
    {
        <= 0 => null,
        >= (long)Gigabyte => string.Create(CultureInfo.InvariantCulture, $"{bytes / Gigabyte:0.0} GB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / Megabyte:0} MB"),
    };

    // "1:02:33", "12:05".
    private static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Floor(seconds));
        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}:{time.Seconds:00}");
    }

    // The file the viewer played last, else the first one.
    private static FileVersion? PlayedVersion(CatalogItemDetail item) =>
        item.Versions.FirstOrDefault(v => v.FileId.Length > 0 && v.FileId == item.UserData?.LastFileId) ?? item.Versions.FirstOrDefault();

    private static CatalogItemOverlay OverlayOf(FileVersion version) =>
        new() { Resolution = version.Resolution, Hdr = version.Hdr ? "HDR" : null };

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
