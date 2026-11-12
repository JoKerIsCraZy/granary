using System.Globalization;
using Granary.Core.Api.Catalog;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Cards;

namespace Granary.Core.ViewModels.Detail;

/// <summary>
/// The detail page's texts per item type: the title, breadcrumbs, the meta line, the file's line, the Info dialog and
/// the labels of the primary actions, worded like Silo's web client (web/src/pages/ItemDetail/*Content.tsx) where it
/// has them.
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
        return item.Type == CatalogItemTypes.Season && DetailText.IsOwnSeasonName(item.SeasonNumber ?? 0, item.Title) ? item.Title.Trim() : null;
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
        string seriesTitle = string.IsNullOrWhiteSpace(item.SeriesTitle) ? AppText.Get("web.detail.hero.typeSeries") : item.SeriesTitle.Trim();
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
            crumbs.Add(new DetailCrumb(AppText.Get("web.detail.episodes.numbered", ("episode", episode)), null, IsFirst: false));
        }
        return crumbs;
    }

    /// <summary>
    /// The primary button. A movie or episode resumes when it has a resume point. A series or season resumes
    /// an episode in progress, plays the next one when partly watched, and otherwise starts at the beginning
    /// (web: resolveLeafPrimaryAction, resolveSeriesPrimaryAction: "Start From Episode 1", or "Browse Series"
    /// with nothing to play; the season's "Play First Episode"; an episode's "Play Episode").
    /// </summary>
    public static DetailPlayAction PlayAction(CatalogItemDetail item, WatchRollup? watch, string? firstEpisodeId = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Type is CatalogItemTypes.Series or CatalogItemTypes.Season)
        {
            string? next = Blank(item.PlayContentId);
            if (next is not null && (watch is { InProgressCount: > 0 } || watch?.IsInProgress == true))
            {
                return new DetailPlayAction(AppText.Get("web.detail.actions.resume"), next, Resumes: true);
            }
            if (next is not null && watch is { WatchedCount: > 0, Played: false })
            {
                return new DetailPlayAction(AppText.Get("web.detail.actions.playNext"), next);
            }
            if (item.Type == CatalogItemTypes.Season)
            {
                return new DetailPlayAction(AppText.Get("web.detail.actions.playFirstEpisode"), Blank(firstEpisodeId) ?? next);
            }
            return next is null
                ? new DetailPlayAction(AppText.Get("web.detail.actions.browseSeries"), null)
                : new DetailPlayAction(AppText.Get("web.detail.actions.startFromFirstEpisode", ("episode", 1)), next);
        }
        string target = Blank(item.PlayContentId) ?? item.ContentId;
        // Watched items store position 0, so any position is a live resume point, a rewatch included.
        if (watch?.IsInProgress == true || watch?.PositionSeconds > 0)
        {
            return new DetailPlayAction(AppText.Get("web.detail.actions.resume"), target, Resumes: true);
        }
        return new DetailPlayAction(item.Type == CatalogItemTypes.Episode
            ? AppText.Get("web.detail.actions.playEpisode")
            : AppText.Get("web.detail.actions.play"), target);
    }

    /// <summary>The web's getWatchedActionLabel (pages/ItemDetail/watchedState.ts).</summary>
    public static string WatchedLabel(string type, bool isWatched) => type switch
    {
        CatalogItemTypes.Series => isWatched
            ? AppText.Get("web.detail.actions.markSeriesUnwatched") : AppText.Get("web.detail.actions.markSeriesWatched"),
        CatalogItemTypes.Season => isWatched
            ? AppText.Get("web.detail.actions.markSeasonUnwatched") : AppText.Get("web.detail.actions.markSeasonWatched"),
        _ => isWatched ? AppText.Get("web.detail.actions.markUnwatched") : AppText.Get("web.detail.actions.markWatched"),
    };

    // How many genres the meta line names; the Info dialog lists them all.
    private const int MetaGenres = 3;
    private const int InfoCast = 25;
    private const double Gigabyte = 1024d * 1024 * 1024;
    private const double Megabyte = 1024d * 1024;
    private const double Kilobyte = 1024d;

    /// <summary>
    /// The one line under the title: a movie's "2026 · 1h 58m · R · Mystery, Thriller", a
    /// series' air years, rating, regular seasons ("3 Seasons") and genres, a season's year and episodes ("10 Episodes"),
    /// an episode's "S3 E1 · 47m · 8 Jan 2021 · TV-MA", the counts in the words of the web's MetadataBadges. Unknown parts
    /// are left out. With <paramref name="showsAdvisoryAge"/>, a movie's or series' <see cref="AdvisoryAge"/> follows its
    /// rating, as the web's MetadataBadges places it.
    /// </summary>
    public static string MetaLine(CatalogItemDetail item, IReadOnlyList<Season>? seasons = null, int? listedEpisodes = null,
        bool showsAdvisoryAge = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        string? genres = item.Genres.Count > 0 ? string.Join(", ", item.Genres.Where(g => !string.IsNullOrWhiteSpace(g)).Take(MetaGenres)) : null;
        string? advisory = showsAdvisoryAge ? AdvisoryAge(item) : null;
        string?[] parts = item.Type switch
        {
            CatalogItemTypes.Series =>
            [
                DetailText.AirYears(item.FirstAirDate, item.LastAirDate, item.Year),
                item.ContentRating,
                advisory,
                SeasonsBadge(seasons?.Count(s => !IsSpecials(s)) is > 0 and var regular ? regular : item.SeasonCount),
                genres,
            ],
            CatalogItemTypes.Season =>
            [
                DetailText.YearOf(item.AirDate) ?? DetailText.YearOf(item.ReleaseDate) ?? DetailText.Year(item.Year),
                EpisodesBadge(item.EpisodeCount ?? listedEpisodes),
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
                item.Type == CatalogItemTypes.Movie ? advisory : null,
                genres,
            ],
        };
        return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
    }

    /// <summary>
    /// A suggested minimum viewer age as the web's MetadataBadges words it: "Common Sense 13+", "MDBList 16+", or "13+" from
    /// a source it does not name; null without a positive age. It is not the certification, only shown beside it.
    /// </summary>
    public static string? AdvisoryAge(CatalogItemDetail item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.AdvisoryAge is not { } age || age <= 0)
        {
            return null;
        }
        // The sources' own names, which no language translates.
        string? source = item.AdvisorySource?.Trim() switch
        {
            "commonsense" => "Common Sense",
            "mdblist" => "MDBList",
            _ => null,
        };
        return source is null
            ? AppText.Get("web.detail.badges.advisory", ("age", age))
            : AppText.Get("web.detail.badges.advisorySource", ("source", source), ("age", age));
    }

    /// <summary>
    /// The small line under the buttons: "Available version: 720P · H264 · EAC3 · 9.3 GB" for one file; null for a series
    /// or season, and for several files, which the Version picker lists instead. Never the file's name or path.
    /// </summary>
    public static string? AvailableVersionLine(CatalogItemDetail item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Versions.Count != 1)
        {
            return null;
        }
        var version = item.Versions[0];
        string line = string.Join(" · ", new[]
        {
            MediaCardFactory.ResolutionText(OverlayOf(version)),
            Upper(version.CodecVideo),
            Upper(version.CodecAudio),
            SizeText(version.FileSize),
        }.Where(p => !string.IsNullOrEmpty(p)));
        return line.Length > 0 ? AppText.Get("granary.detail.availableVersion", ("version", line)) : null;
    }

    /// <summary>The primary button's words: a movie or episode says where it resumes ("Resume at 1:02:33"), else the action's own.</summary>
    public static string PlayButtonText(CatalogItemDetail item, WatchRollup? watch, DetailPlayAction action)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(action);
        bool leaf = item.Type is not (CatalogItemTypes.Series or CatalogItemTypes.Season);
        return leaf && action.Resumes && watch?.PositionSeconds is >= 1 and var position
            ? AppText.Get("web.detail.actions.resumeAt", ("time", Clock(position)))
            : action.Label;
    }

    /// <summary>
    /// The Info dialog: the details (with the full overview, a series' regular seasons and episodes once its season list
    /// is read, a season's number and episodes), the cast, the crew by job, and each file's facts with the played file's
    /// audio as its badge words it ("EAC3 5.1"). File names and paths are left out: they are administrator data.
    /// Sections without rows are left out; so is the overview with <paramref name="withOverview"/> false, an unwatched
    /// episode's the profile hides.
    /// </summary>
    public static IReadOnlyList<DetailInfoSection> InfoSections(CatalogItemDetail item, IReadOnlyList<Season>? seasons = null, int? listedEpisodes = null,
        bool withOverview = true)
    {
        // Counted like the server's season_count: regular seasons, without the specials.
        var regular = seasons?.Where(s => !IsSpecials(s)).ToList();
        int? seasonCount = regular is { Count: > 0 } ? regular.Count : item.SeasonCount;
        int? episodeCount = regular is { Count: > 0 } ? regular.Sum(s => s.EpisodeCount) : item.EpisodeCount;
        var played = PlayedVersion(item);
        ArgumentNullException.ThrowIfNull(item);
        var sections = new List<DetailInfoSection>();
        // The labels are the web's for the same fields where it has them: its metadata editor's and its Media Info's.
        Add(AppText.Get("granary.detail.info.details"),
        [
            Row(AppText.Get("web.item.dialog.fieldTitle"), DisplayTitle(item)),
            Row(AppText.Get("web.item.editMetadata.originalTitle"),
                item.OriginalTitle is { } original && original.Trim() != item.Title.Trim() ? original : null),
            item.Type == CatalogItemTypes.Series
                ? Row(AppText.Get("granary.detail.info.aired"), DetailText.AirYears(item.FirstAirDate, item.LastAirDate, item.Year))
                : Row(AppText.Get("granary.detail.info.released"), item.Type == CatalogItemTypes.Movie
                    ? DetailText.Date(item.ReleaseDate) ?? DetailText.Year(item.Year)
                    : DetailText.Date(item.AirDate ?? item.ReleaseDate)),
            Row(AppText.Get("granary.detail.info.season"), item.Type == CatalogItemTypes.Season ? DetailText.SeasonLabel(item.SeasonNumber ?? 0) : null),
            Row(AppText.Get("web.detail.seasons.heading"), item.Type == CatalogItemTypes.Series ? SeasonsBadge(seasonCount) : null),
            Row(AppText.Get("web.detail.episodes.heading"), item.Type switch
            {
                CatalogItemTypes.Series => EpisodesBadge(episodeCount),
                CatalogItemTypes.Season => EpisodesBadge(item.EpisodeCount ?? listedEpisodes),
                _ => null,
            }),
            Row(AppText.Get("granary.detail.info.runtime"), DetailText.Runtime(RuntimeMinutes(item))),
            Row(AppText.Get("web.item.editMetadata.contentRating"), item.ContentRating),
            Row(AppText.Get("web.item.editMetadata.genres"), Joined(item.Genres)),
            Row(AppText.Get("web.item.editMetadata.countries"), Joined(item.Countries)),
            Row(AppText.Get("web.item.editMetadata.studios"), Joined(item.Studios)),
            Row(AppText.Get("web.item.editMetadata.networks"), Joined(item.Networks)),
            Row(AppText.Get("web.item.editMetadata.ratings"),
                Ratings(item) is { Count: > 0 } ratings ? string.Join(" · ", ratings.Select(r => r.AccessibleName)) : null),
            Row(AppText.Get("web.item.editMetadata.tagline"), item.Tagline),
            Row(AppText.Get("web.item.editMetadata.overview"), withOverview ? item.Overview : null, isSelectable: true),
        ]);
        Add(AppText.Get("web.detail.sections.cast"), [.. item.Cast
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Name))
            .OrderBy(c => c.Order)
            .Take(InfoCast)
            .Select(c => Row(c.Name, string.IsNullOrWhiteSpace(c.Character) ? "—" : c.Character))]);
        // The jobs as the server names them: values of the server, as the web's CrewList shows those it has no words for.
        Add(AppText.Get("web.detail.castCrew.crew"), [.. item.Crew
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Job))
            .GroupBy(c => c.Job.Trim(), StringComparer.Ordinal)
            .Select(g => Row(g.Key, string.Join(", ", g.Select(c => c.Name.Trim()).Distinct(StringComparer.Ordinal))))]);
        for (int i = 0; i < item.Versions.Count; i++)
        {
            var version = item.Versions[i];
            // Several files as the web's Media Info numbers them: "Version 2".
            Add(item.Versions.Count == 1 ? AppText.Get("granary.detail.info.file") : AppText.Get("web.detail.versions.numbered", ("number", i + 1)),
            [
                Row(AppText.Get("web.detail.mediaInfo.row.edition"), version.EditionRaw),
                Row(AppText.Get("web.detail.mediaInfo.row.resolution"), MediaCardFactory.ResolutionText(OverlayOf(version))),
                Row(AppText.Get("web.detail.mediaInfo.video"), Upper(version.CodecVideo)),
                Row(AppText.Get("web.detail.mediaInfo.audio"), ReferenceEquals(version, played)
                    ? MediaCardFactory.AudioText(item.OverlaySummary) ?? Upper(version.CodecAudio)
                    : Upper(version.CodecAudio)),
                Row(AppText.Get("web.detail.mediaInfo.row.container"), Upper(version.Container)),
                Row(AppText.Get("web.detail.mediaInfo.row.fileSize"), SizeText(version.FileSize)),
                Row(AppText.Get("web.detail.mediaInfo.row.bitrate"),
                    version.Bitrate > 0 ? string.Create(CultureInfo.InvariantCulture, $"{version.Bitrate / 1000d:0.#} Mb/s") : null),
                Row(AppText.Get("web.detail.mediaInfo.row.duration"),
                    version.Duration > 0 ? DetailText.Runtime((int)Math.Round(version.Duration / 60d, MidpointRounding.AwayFromZero)) : null),
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

        static DetailInfoRow? Row(string label, string? value, bool isSelectable = false) =>
            string.IsNullOrWhiteSpace(value) ? null : new DetailInfoRow(label.Trim(), value.Trim(), isSelectable);

        static string? Joined(IReadOnlyList<string> values) =>
            values.Any(v => !string.IsNullOrWhiteSpace(v)) ? string.Join(", ", values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())) : null;
    }

    // "3 Seasons", "10 Episodes", as the web's MetadataBadges count them; null without a count.
    internal static string? SeasonsBadge(int? count) => count is > 0 and var n
        ? AppText.Get("web.detail.badges.seasons", ("count", n), ("count_text", AppText.Number(n)))
        : null;

    internal static string? EpisodesBadge(int? count) => count is > 0 and var n
        ? AppText.Get("web.detail.badges.episodes", ("count", n), ("count_text", AppText.Number(n)))
        : null;

    private static string? Upper(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim().ToUpperInvariant();

    // "9.3 GB", "700 MB", "12 KB": binary units under Windows' names, as Explorer shows them. The unit is picked after
    // rounding, so a file a byte short of 1 GB reads "1.0 GB", not "1024 MB".
    private static string? SizeText(long bytes)
    {
        if (bytes <= 0)
        {
            return null;
        }
        if (Math.Round(bytes / Megabyte) >= 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / Gigabyte:0.0} GB");
        }
        if (Math.Round(bytes / Kilobyte) >= 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / Megabyte:0} MB");
        }
        return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, Math.Round(bytes / Kilobyte)):0} KB");
    }

    // "1:02:33", "12:05".
    internal static string Clock(double seconds)
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
