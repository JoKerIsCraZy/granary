using System.Globalization;
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

    /// <summary>"Pick a genre".</summary>
    public string? Placeholder { get; init; }

    /// <summary>"min", "kbps".</summary>
    public string? Unit { get; init; }

    /// <summary>original, audio or subtitle.</summary>
    public string? LanguageSource { get; init; }

    public bool ShowsOnly { get; init; }

    public bool EpisodesOnly { get; init; }

    /// <summary>Only on a server with <c>extended_query_rules</c>.</summary>
    public bool Extended { get; init; }

    public bool NoEpisodeValue { get; init; }
}

/// <summary>The fields, groups, kinds of title and sorts of a smart collection's rules.</summary>
public static class CollectionRuleFields
{
    /// <summary>The conditions that take a span ("30d") rather than a date.</summary>
    public static readonly IReadOnlySet<string> RelativeDateOperators = new HashSet<string>(StringComparer.Ordinal) { "in_last", "not_in_last" };

    public static IReadOnlyList<(string Key, string Label)> Groups { get; } =
        [("title", "Title"), ("people", "People"), ("file", "File"), ("you", "You"), ("library", "Library")];

    /// <summary>The kinds of title, lower-case as the rules' sentence reads them ("Show movies from …").</summary>
    public static IReadOnlyList<SettingChoice> Scopes { get; } =
    [
        new("all", "all titles"), new("video", "movies and shows"), new("movie", "movies"), new("series", "shows"),
        new("episode", "episodes"), new("audiobook", "audiobooks"), new("ebook", "ebooks"), new("manga", "manga"),
    ];

    public static IReadOnlyList<SettingChoice> InLastUnits { get; } = [new("d", "days"), new("w", "weeks"), new("m", "months"), new("y", "years")];

    public static IReadOnlyList<SettingChoice> YesNo { get; } = [new("true", "Yes"), new("false", "No")];

    private static readonly RuleOperator[] IsOperators = [new("is", "is"), new("is_not", "is not")];

    private static readonly RuleOperator[] NumberOperators =
        [new("gte", "is at least"), new("lte", "is at most"), new("gt", "is above"), new("lt", "is below"), new("between", "is between")];

    private static readonly RuleOperator[] TextOperators =
    [
        new("contains", "contains"), new("not_contains", "does not contain"), new("is", "is"), new("is_not", "is not"),
        new("begins_with", "begins with"), new("ends_with", "ends with"),
    ];

    private static readonly RuleOperator[] DateOperators =
        [new("in_last", "is in the last"), new("not_in_last", "is not in the last"), new("lt", "is before"), new("gt", "is after"), new("between", "is between")];

    private static readonly RuleOperator[] SwitchOperators = [new("is", "is")];

    /// <summary>In field-picker order: Title, People, File, You, Library.</summary>
    public static IReadOnlyList<RuleField> All { get; } =
    [
        new("title", "Title", "title", TextOperators, RuleInput.Text) { Extended = true },
        new("show_title", "Show title", "title", TextOperators, RuleInput.Text) { EpisodesOnly = true },
        Facet("genre", "Genre", "genre", [.. IsOperators, new("contains", "contains", Hidden: true)]),
        Facet("studio", "Studio", "studio"),
        Facet("network", "Network", "network"),
        Facet("country", "Country", "country"),
        Facet("content_rating", "Content rating", "rating"),
        Language("original_language", "Original language", "title", "original"),
        new("type", "Type", "title", IsOperators, RuleInput.Select) { Options = [new("movie", "Movies"), new("series", "Shows")] },
        new("year", "Year", "title", [.. IsOperators, .. NumberOperators], RuleInput.Number) { IsNumber = true, SupportsRange = true },
        new("decade", "Decade", "title", IsOperators, RuleInput.Select) { IsNumber = true, Options = Decades(), Extended = true },
        Date("release_date", "Release date", "title"),
        Date("last_air_date", "Latest episode aired", "title") with { ShowsOnly = true, Extended = true },
        new("runtime", "Duration", "title", NumberOperators, RuleInput.Number) { IsNumber = true, SupportsRange = true, Unit = "min", Extended = true },
        Rating("rating_imdb", "IMDb rating"),
        Rating("rating_tmdb", "TMDB rating") with { Extended = true },
        Rating("rating_rt_critic", "RT critic score") with { Extended = true, NoEpisodeValue = true },
        Rating("rating_rt_audience", "RT audience score") with { Extended = true, NoEpisodeValue = true },
        new("status", "Status", "title", IsOperators, RuleInput.Select)
        {
            Options = [new("pending", "pending"), new("matched", "matched"), new("unmatched", "unmatched")],
            Hidden = true,
        },
        Person("actor", "Actor"),
        Person("director", "Director"),
        Person("writer", "Writer"),
        Person("producer", "Producer"),
        new("resolution", "Resolution", "file", IsOperators, RuleInput.Select)
        {
            Options = [.. new[] { "480p", "720p", "1080p", "2160p", "4320p" }.Select(r => new SettingChoice(r, r))],
        },
        Switch("hdr", "HDR", "file"),
        Switch("dolby_vision", "Dolby Vision", "file"),
        Language("audio_language", "Audio language", "file", "audio"),
        Language("subtitle_language", "Subtitle language", "file", "subtitle"),
        new("bitrate", "Bitrate", "file", NumberOperators, RuleInput.Number) { IsNumber = true, SupportsRange = true, Unit = "kbps" },
        Switch("watched", "Watched", "you") with { Personalized = true },
        Switch("favorited", "Favorited", "you") with { Personalized = true },
        Switch("in_watchlist", "In watchlist", "you") with { Personalized = true },
        Switch("in_progress", "In progress", "you") with { Personalized = true },
        Date("last_watched", "Last watched", "you") with { Personalized = true },
        Date("added_at", "Added", "library"),
        Date("latest_episode_added", "Latest episode added", "library") with { ShowsOnly = true, Extended = true },
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
        return [.. SortOptions.Where(s => s.Value == keep || Applies(s, relevance)).Select(s => new SettingChoice(s.Value, s.Label))];
    }

    /// <summary>How each direction reads for a sort: "A–Z", "Newest first", "Highest first".</summary>
    public static (string Desc, string Asc) Directions(string field) => field switch
    {
        "title" or "author" or "narrator" or "series" => ("Z–A", "A–Z"),
        "added_at" or "release_date" or "last_air_date" or "latest_episode_added" or "year" or "date_viewed" => ("Newest first", "Oldest first"),
        _ => ("Highest first", "Lowest first"),
    };

    /// <summary>"Title A–Z", "Date Added, newest first": a saved default sort, as its line names it.</summary>
    public static string SortLabel(string field, string? order)
    {
        string name = SortOptions.FirstOrDefault(s => s.Value == field)?.Label ?? field;
        var (desc, asc) = Directions(field);
        string direction = (order is "asc" or "desc" ? order : Api.Catalog.SmartSort.DefaultOrder(field)) == "asc" ? asc : desc;
        return field is "title" or "author" or "narrator" or "series" ? $"{name} {direction}" : $"{name}, {direction.ToLowerInvariant()}";
    }

    private sealed record SortOption(string Value, string Label, string[] Scopes);

    private static readonly string[] VideoScopes = ["movie", "series", "episode"];
    private static readonly string[] MediaScopes = [.. VideoScopes, "audiobook", "ebook"];

    private static readonly SortOption[] SortOptions =
    [
        new("title", "Title", [.. MediaScopes, "manga"]),
        new("added_at", "Date Added", [.. MediaScopes, "manga"]),
        new("release_date", "Release Date", [.. MediaScopes, "manga"]),
        new("last_air_date", "Latest Episode Air Date", ["series", "episode"]),
        new("latest_episode_added", "Latest Episode Added", ["series"]),
        new("year", "Year", [.. MediaScopes, "manga"]),
        new("content_rating", "Content Rating", VideoScopes),
        new("runtime", "Duration", MediaScopes),
        new("rating_imdb", "IMDb Rating", VideoScopes),
        new("rating_tmdb", "TMDB Rating", VideoScopes),
        new("rating_rt_critic", "RT Critic Rating", VideoScopes),
        new("rating_rt_audience", "RT Audience Rating", VideoScopes),
        new("resolution", "Resolution", VideoScopes),
        new("bitrate", "Bitrate", MediaScopes),
        new("progress", "Progress", [.. MediaScopes, "manga"]),
        new("date_viewed", "Date Viewed", [.. MediaScopes, "manga"]),
        new("plays", "Plays", [.. MediaScopes, "manga"]),
        new("author", "Author", ["audiobook", "ebook", "manga"]),
        new("narrator", "Narrator", ["audiobook"]),
        new("series", "Series", ["audiobook", "ebook"]),
    ];

    // "all" is the video baseline: a sort offered there applies to movies, shows and episodes alike.
    private static bool Applies(SortOption sort, string relevance) =>
        relevance == "all" ? VideoScopes.All(sort.Scopes.Contains) : sort.Scopes.Contains(relevance);

    private static RuleField Relabelled(RuleField field, string scope) => (field.Value, scope) switch
    {
        ("title", "episode") => field with { Label = "Episode title" },
        ("watched", "ebook" or "manga") => field with { Label = "Read" },
        ("last_watched", "ebook" or "manga") => field with { Label = "Last read" },
        _ => field,
    };

    private static RuleField Facet(string value, string label, string noun, RuleOperator[]? operators = null) =>
        new(value, label, "title", operators ?? IsOperators, RuleInput.Facet) { Facet = value, Placeholder = $"Pick a {noun}" };

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
            decades.Add(new SettingChoice(decade.ToString(CultureInfo.InvariantCulture), $"{decade.ToString(CultureInfo.InvariantCulture)}s"));
        }
        return decades;
    }
}
