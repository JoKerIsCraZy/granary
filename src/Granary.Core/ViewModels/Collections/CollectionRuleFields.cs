using System.Globalization;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Settings;

namespace Granary.Core.ViewModels.Collections;

/// <summary>How a rule's value is entered.</summary>
public enum RuleInput
{
    Text,
    Number,
    Select,
    Boolean,
    Person,
    Facet,
    Date,
    Language,
}

/// <param name="Hidden">Kept for saved rules that use it, but not offered for a new one.</param>
public sealed record RuleOperator(string Value, string Label, bool Hidden = false);

/// <summary>
/// A field a smart collection's rule can compare, as the web's <c>collectionBuilderFields.ts</c> has it: its picker group,
/// conditions, how its value is entered, and where it is offered.
/// </summary>
public sealed record RuleField(string Value, string Label, string Group, IReadOnlyList<RuleOperator> Operators, RuleInput Input)
{
    /// <summary>A number value (also for select fields such as the decade).</summary>
    public bool IsNumber { get; init; }

    public bool SupportsRange { get; init; }

    public IReadOnlyList<SettingChoice> Options { get; init; } = [];

    /// <summary>Rules about the profile (watched, favourited, …).</summary>
    public bool Personalized { get; init; }

    public bool Hidden { get; init; }

    /// <summary>The catalog facet a value is picked from: genre, studio, network, country, content_rating.</summary>
    public string? Facet { get; init; }

    /// <summary>"Pick a genre", in the language on screen.</summary>
    public string? Placeholder { get; init; }

    /// <summary>"min", "kbps", in the language on screen.</summary>
    public string? Unit { get; init; }

    /// <summary>original, audio or subtitle.</summary>
    public string? LanguageSource { get; init; }

    public bool ShowsOnly { get; init; }

    public bool EpisodesOnly { get; init; }

    /// <summary>Only on a server with <c>extended_query_rules</c>.</summary>
    public bool Extended { get; init; }

    public bool NoEpisodeValue { get; init; }
}

/// <summary>
/// The fields, groups, kinds of title and sorts of a smart collection's rules, in the words of the web's
/// <c>collectionBuilderFields.ts</c>, <c>RuleBuilder</c> and <c>OrderBlock</c>. Every list is built when asked for, in the
/// language on screen; a view model keeps the lists its boxes pick from.
/// </summary>
public static class CollectionRuleFields
{
    /// <summary>The conditions that take a span ("30d") rather than a date.</summary>
    public static readonly IReadOnlySet<string> RelativeDateOperators = new HashSet<string>(StringComparer.Ordinal) { "in_last", "not_in_last" };

    public static IReadOnlyList<(string Key, string Label)> Groups =>
    [
        ("title", AppText.Get("web.collections.builder.groupTitle")),
        ("people", AppText.Get("web.collections.builder.groupPeople")),
        ("file", AppText.Get("web.collections.builder.groupFile")),
        ("you", AppText.Get("web.collections.builder.groupYou")),
        ("library", AppText.Get("web.collections.builder.groupLibrary")),
    ];

    /// <summary>The kinds of title, lower-case as the rules' sentence reads them ("Show movies from …").</summary>
    public static IReadOnlyList<SettingChoice> Scopes =>
    [
        new("all", AppText.Get("web.home.addRow.rules.scopeAll")),
        new("video", AppText.Get("web.home.addRow.rules.scopeVideo")),
        new("movie", AppText.Get("web.home.addRow.rules.scopeMovie")),
        new("series", AppText.Get("web.home.addRow.rules.scopeSeries")),
        new("episode", AppText.Get("web.home.addRow.rules.scopeEpisode")),
        new("audiobook", AppText.Get("web.home.addRow.rules.scopeAudiobook")),
        new("ebook", AppText.Get("web.home.addRow.rules.scopeEbook")),
        new("manga", AppText.Get("web.home.addRow.rules.scopeManga")),
    ];

    public static IReadOnlyList<SettingChoice> InLastUnits =>
    [
        new("d", AppText.Get("web.catalog.rules.unitDays")),
        new("w", AppText.Get("web.catalog.rules.unitWeeks")),
        new("m", AppText.Get("web.catalog.rules.unitMonths")),
        new("y", AppText.Get("web.catalog.rules.unitYears")),
    ];

    public static IReadOnlyList<SettingChoice> YesNo => [new("true", AppText.Get("web.catalog.rules.yes")), new("false", AppText.Get("web.catalog.rules.no"))];

    private static RuleOperator Is => new("is", AppText.Get("web.collections.builder.opIs"));

    private static RuleOperator IsNot => new("is_not", AppText.Get("web.collections.builder.opIsNot"));

    private static RuleOperator Between => new("between", AppText.Get("web.collections.builder.opBetween"));

    private static RuleOperator[] IsOperators => [Is, IsNot];

    private static RuleOperator[] NumberOperators =>
    [
        new("gte", AppText.Get("web.collections.builder.opAtLeast")),
        new("lte", AppText.Get("web.collections.builder.opAtMost")),
        new("gt", AppText.Get("web.collections.builder.opAbove")),
        new("lt", AppText.Get("web.collections.builder.opBelow")),
        Between,
    ];

    private static RuleOperator[] TextOperators =>
    [
        new("contains", AppText.Get("web.collections.builder.opContains")),
        new("not_contains", AppText.Get("web.collections.builder.opNotContains")),
        Is,
        IsNot,
        new("begins_with", AppText.Get("web.collections.builder.opBeginsWith")),
        new("ends_with", AppText.Get("web.collections.builder.opEndsWith")),
    ];

    private static RuleOperator[] DateOperators =>
    [
        new("in_last", AppText.Get("web.collections.builder.opInLast")),
        new("not_in_last", AppText.Get("web.collections.builder.opNotInLast")),
        new("lt", AppText.Get("web.collections.builder.opBefore")),
        new("gt", AppText.Get("web.collections.builder.opAfter")),
        Between,
    ];

    private static RuleOperator[] SwitchOperators => [Is];

    /// <summary>In field-picker order: Title, People, File, You, Library.</summary>
    public static IReadOnlyList<RuleField> All =>
    [
        new("title", AppText.Get("web.collections.builder.fieldTitle"), "title", TextOperators, RuleInput.Text) { Extended = true },
        new("show_title", AppText.Get("web.collections.builder.fieldShowTitle"), "title", TextOperators, RuleInput.Text) { EpisodesOnly = true },
        Facet("genre", AppText.Get("web.collections.builder.fieldGenre"),
            [.. IsOperators, new("contains", AppText.Get("web.collections.builder.opContains"), Hidden: true)]),
        Facet("studio", AppText.Get("web.collections.builder.fieldStudio")),
        Facet("network", AppText.Get("web.collections.builder.fieldNetwork")),
        Facet("country", AppText.Get("web.collections.builder.fieldCountry")),
        Facet("content_rating", AppText.Get("web.collections.builder.fieldContentRating")),
        Language("original_language", AppText.Get("web.collections.builder.fieldOriginalLanguage"), "title", "original"),
        new("type", AppText.Get("web.collections.builder.fieldType"), "title", IsOperators, RuleInput.Select)
        {
            Options = [new("movie", AppText.Get("web.collections.builder.typeMovies")), new("series", AppText.Get("web.collections.builder.typeShows"))],
        },
        new("year", AppText.Get("web.collections.builder.fieldYear"), "title", [.. IsOperators, .. NumberOperators], RuleInput.Number)
        {
            IsNumber = true,
            SupportsRange = true,
        },
        new("decade", AppText.Get("web.collections.builder.fieldDecade"), "title", IsOperators, RuleInput.Select)
        {
            IsNumber = true,
            Options = Decades(),
            Extended = true,
        },
        Date("release_date", AppText.Get("web.collections.builder.fieldReleaseDate"), "title"),
        Date("last_air_date", AppText.Get("web.collections.builder.fieldLastAirDate"), "title") with { ShowsOnly = true, Extended = true },
        new("runtime", AppText.Get("web.collections.builder.fieldRuntime"), "title", NumberOperators, RuleInput.Number)
        {
            IsNumber = true,
            SupportsRange = true,
            Unit = AppText.Get("web.collections.builder.unitMinutes"),
            Extended = true,
        },
        Rating("rating_imdb", AppText.Get("web.collections.builder.fieldRatingImdb")),
        Rating("rating_tmdb", AppText.Get("web.collections.builder.fieldRatingTmdb")) with { Extended = true },
        Rating("rating_rt_critic", AppText.Get("web.collections.builder.fieldRatingRtCritic")) with { Extended = true, NoEpisodeValue = true },
        Rating("rating_rt_audience", AppText.Get("web.collections.builder.fieldRatingRtAudience")) with { Extended = true, NoEpisodeValue = true },
        new("status", AppText.Get("web.collections.builder.fieldStatus"), "title", IsOperators, RuleInput.Select)
        {
            Options =
            [
                new("pending", AppText.Get("web.collections.builder.statusPending")),
                new("matched", AppText.Get("web.collections.builder.statusMatched")),
                new("unmatched", AppText.Get("web.collections.builder.statusUnmatched")),
            ],
            Hidden = true,
        },
        Person("actor", AppText.Get("web.collections.builder.fieldActor")),
        Person("director", AppText.Get("web.collections.builder.fieldDirector")),
        Person("writer", AppText.Get("web.collections.builder.fieldWriter")),
        Person("producer", AppText.Get("web.collections.builder.fieldProducer")),
        new("resolution", AppText.Get("web.collections.builder.fieldResolution"), "file", IsOperators, RuleInput.Select)
        {
            Options = [.. new[] { "480p", "720p", "1080p", "2160p", "4320p" }.Select(r => new SettingChoice(r, r))],
        },
        Switch("hdr", AppText.Get("web.collections.builder.fieldHdr"), "file"),
        Switch("dolby_vision", AppText.Get("web.collections.builder.fieldDolbyVision"), "file"),
        Language("audio_language", AppText.Get("web.collections.builder.fieldAudioLanguage"), "file", "audio"),
        Language("subtitle_language", AppText.Get("web.collections.builder.fieldSubtitleLanguage"), "file", "subtitle"),
        new("bitrate", AppText.Get("web.collections.builder.fieldBitrate"), "file", NumberOperators, RuleInput.Number)
        {
            IsNumber = true,
            SupportsRange = true,
            Unit = AppText.Get("web.collections.builder.unitKbps"),
        },
        Switch("watched", AppText.Get("web.collections.builder.fieldWatched"), "you") with { Personalized = true },
        Switch("favorited", AppText.Get("web.collections.builder.fieldFavorited"), "you") with { Personalized = true },
        Switch("in_watchlist", AppText.Get("web.collections.builder.fieldInWatchlist"), "you") with { Personalized = true },
        Switch("in_progress", AppText.Get("web.collections.builder.fieldInProgress"), "you") with { Personalized = true },
        Date("last_watched", AppText.Get("web.collections.builder.fieldLastWatched"), "you") with { Personalized = true },
        Date("added_at", AppText.Get("web.collections.builder.fieldAdded"), "library"),
        Date("latest_episode_added", AppText.Get("web.collections.builder.fieldLatestEpisodeAdded"), "library") with { ShowsOnly = true, Extended = true },
    ];

    /// <summary>A field by its name ("rating" is the IMDb rating); null for one this app does not know.</summary>
    public static RuleField? Find(string field) => All.FirstOrDefault(f => f.Value == (field == "rating" ? "rating_imdb" : field));

    /// <summary>
    /// The fields a new rule can pick for a kind of title, as the web's <c>getFilterRuleFieldOptions</c>: the newer ones
    /// only on a server that takes them, show fields where shows can match, episode fields only for episodes.
    /// </summary>
    public static IReadOnlyList<RuleField> Offered(string scope, bool extendedRules, bool showTitleRules) =>
    [
        .. All.Where(f => !f.Hidden
                && (extendedRules || !f.Extended)
                && (showTitleRules || !f.EpisodesOnly)
                && (!f.ShowsOnly || scope is "all" or "video" or "series")
                && (!f.EpisodesOnly || scope == "episode")
                && (!f.NoEpisodeValue || scope != "episode"))
            .Select(f => Relabelled(f, scope)),
    ];

    /// <summary>The value a rule starts with when its field or condition changes.</summary>
    public static System.Text.Json.JsonElement DefaultValue(string field, string op)
    {
        var definition = Find(field);
        if (op == "between" && definition is { SupportsRange: true })
        {
            return Api.Catalog.SmartValue.Range(Api.Catalog.SmartValue.Text(""), Api.Catalog.SmartValue.Text(""));
        }
        return definition?.Input switch
        {
            RuleInput.Boolean => Api.Catalog.SmartValue.Bool(false),
            RuleInput.Number => Api.Catalog.SmartValue.Number(0),
            _ => Api.Catalog.SmartValue.Text(""),
        };
    }

    /// <summary>The sorts a smart collection offers for a kind of title, as the web's <c>getQuerySortOptions</c>.</summary>
    public static IReadOnlyList<SettingChoice> Sorts(string? scope, string keep)
    {
        string relevance = scope switch
        {
            null or "all" or "video" => "all",
            _ => scope,
        };
        return [.. SortOptions.Where(s => s.Value == keep || Applies(s, relevance)).Select(s => new SettingChoice(s.Value, SortName(s.Value)))];
    }

    /// <summary>A sort's name, as the web's <c>querySortOptions.ts</c> labels it ("Date Added"); its field for one it does not know.</summary>
    public static string SortName(string field) => field switch
    {
        "author" => AppText.Get("catalog.sort.author"),
        "narrator" => AppText.Get("catalog.sort.narrator"),
        "series" => AppText.Get("catalog.sort.series"),
        _ => new LibrarySortOption(field, StartsDescending: false).Label,
    };

    /// <summary>How each direction reads for a sort: "A–Z", "Newest first", "Highest first".</summary>
    public static (string Desc, string Asc) Directions(string field)
    {
        var (desc, asc) = DirectionsOf(field);
        return (AppText.Get("web.collections.fields.direction", ("direction", desc)), AppText.Get("web.collections.fields.direction", ("direction", asc)));
    }

    /// <summary>"Title A–Z", "Date Added, newest first": a saved default sort, as its line names it.</summary>
    public static string SortLabel(string field, string? order)
    {
        var (desc, asc) = DirectionsOf(field);
        string direction = (order is "asc" or "desc" ? order : Api.Catalog.SmartSort.DefaultOrder(field)) == "asc" ? asc : desc;
        return field is "title" or "author" or "narrator" or "series"
            ? AppText.Get("web.collections.fields.sortAlphabetical", ("name", SortName(field)), ("direction", direction))
            : AppText.Get("web.collections.fields.sortWithDirection", ("name", SortName(field)), ("direction", direction));
    }

    // Which way each direction reads for a sort, as the web's OrderBlock names them: A–Z, newest first, highest first.
    private static (string Desc, string Asc) DirectionsOf(string field) => field switch
    {
        "title" or "author" or "narrator" or "series" => ("za", "az"),
        "added_at" or "release_date" or "last_air_date" or "latest_episode_added" or "year" or "date_viewed" => ("newest", "oldest"),
        _ => ("highest", "lowest"),
    };

    private sealed record SortOption(string Value, string[] Scopes);

    private static readonly string[] VideoScopes = ["movie", "series", "episode"];
    private static readonly string[] MediaScopes = [.. VideoScopes, "audiobook", "ebook"];

    private static readonly SortOption[] SortOptions =
    [
        new("title", [.. MediaScopes, "manga"]),
        new("added_at", [.. MediaScopes, "manga"]),
        new("release_date", [.. MediaScopes, "manga"]),
        new("last_air_date", ["series", "episode"]),
        new("latest_episode_added", ["series"]),
        new("year", [.. MediaScopes, "manga"]),
        new("content_rating", VideoScopes),
        new("runtime", MediaScopes),
        new("rating_imdb", VideoScopes),
        new("rating_tmdb", VideoScopes),
        new("rating_rt_critic", VideoScopes),
        new("rating_rt_audience", VideoScopes),
        new("resolution", VideoScopes),
        new("bitrate", MediaScopes),
        new("progress", [.. MediaScopes, "manga"]),
        new("date_viewed", [.. MediaScopes, "manga"]),
        new("plays", [.. MediaScopes, "manga"]),
        new("author", ["audiobook", "ebook", "manga"]),
        new("narrator", ["audiobook"]),
        new("series", ["audiobook", "ebook"]),
    ];

    // "all" is the video baseline: a sort offered there applies to movies, shows and episodes alike.
    private static bool Applies(SortOption sort, string relevance) =>
        relevance == "all" ? VideoScopes.All(sort.Scopes.Contains) : sort.Scopes.Contains(relevance);

    private static RuleField Relabelled(RuleField field, string scope) => (field.Value, scope) switch
    {
        ("title", "episode") => field with { Label = AppText.Get("web.catalog.rules.fieldEpisodeTitle") },
        ("watched", "ebook" or "manga") => field with { Label = AppText.Get("web.catalog.rules.fieldRead") },
        ("last_watched", "ebook" or "manga") => field with { Label = AppText.Get("web.catalog.rules.fieldLastRead") },
        _ => field,
    };

    // "Pick a genre", … and "Pick a rating" for the content rating, as the web's facetPlaceholder.
    private static RuleField Facet(string value, string label, RuleOperator[]? operators = null) =>
        new(value, label, "title", operators ?? IsOperators, RuleInput.Facet)
        {
            Facet = value,
            Placeholder = AppText.Get("web.collections.builder.facetPlaceholder", ("facet", value)),
        };

    private static RuleField Person(string value, string label) => new(value, label, "people", IsOperators, RuleInput.Person);

    private static RuleField Date(string value, string label, string group) => new(value, label, group, DateOperators, RuleInput.Date) { SupportsRange = true };

    private static RuleField Rating(string value, string label) =>
        new(value, label, "title", NumberOperators, RuleInput.Number) { IsNumber = true, SupportsRange = true };

    private static RuleField Language(string value, string label, string group, string source) =>
        new(value, label, group, IsOperators, RuleInput.Language) { LanguageSource = source };

    private static RuleField Switch(string value, string label, string group) => new(value, label, group, SwitchOperators, RuleInput.Boolean);

    // From this decade back to the 1900s, as each decade's first year.
    private static IReadOnlyList<SettingChoice> Decades()
    {
        int latest = DateTime.UtcNow.Year / 10 * 10;
        var decades = new List<SettingChoice>();
        for (int decade = latest; decade >= 1900; decade -= 10)
        {
            decades.Add(new SettingChoice(decade.ToString(CultureInfo.InvariantCulture),
                AppText.Get("web.collections.guidedRules.decadeOption", ("decade", decade.ToString(CultureInfo.InvariantCulture)))));
        }
        return decades;
    }
}
