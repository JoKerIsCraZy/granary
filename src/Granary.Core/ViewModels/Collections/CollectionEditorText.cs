namespace Granary.Core.ViewModels.Collections;

/// <summary>The collection editor's words, as the web's <c>lib/collections/copy.ts</c> has them.</summary>
public static class CollectionEditorText
{
    public const string NewCollection = "New collection";

    public const string NotCreatedYet = "Not created yet";

    public const string TitlesAlreadySaved = "Titles are already saved.";

    public const string NameItThenCreate = "Name it, then create it.";

    public const string ConflictTitle = "This collection changed since you opened it.";

    public const string Stale = "This collection changed while you were editing. Reload it and review your changes before saving again.";

    /// <summary>"Maya, Leo and Sam".</summary>
    public static string JoinNames(IReadOnlyList<string> names) => names.Count <= 1
        ? names.FirstOrDefault() ?? ""
        : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

    /// <summary>"3 titles", "1 title".</summary>
    public static string Titles(int count) => count == 1 ? "1 title" : $"{count} titles";

    public static string TitlesReadyToAdd(int count) => $"{Titles(count)} ready to add";

    /// <summary>"Name and description not saved": the first field as labelled, the rest in lower case.</summary>
    public static string NotSaved(IReadOnlyList<string> labels) => labels.Count == 0
        ? ""
        : JoinNames([labels[0], .. labels.Skip(1).Select(l => char.ToLowerInvariant(l[0]) + l[1..])]) + " not saved";

    /// <summary>"When you save, Maya and Leo lose it, including rows they made from it."</summary>
    public static string UnshareWarning(IReadOnlyList<string> profiles)
    {
        string who = profiles.Count > 0 ? JoinNames(profiles) : "Other profiles";
        return $"When you save, {who} {(profiles.Count == 1 ? "loses" : "lose")} it, including rows they made from it.";
    }

    /// <summary>"Name and show only changed in both places."</summary>
    public static string ConflictFields(IReadOnlyList<string> labels) => labels.Count == 0
        ? "Save again to keep your changes."
        : JoinNames([labels[0], .. labels.Skip(1).Select(l => char.ToLowerInvariant(l[0]) + l[1..])]) + " changed in both places.";

    /// <summary>The body of the delete question.</summary>
    public static string DeleteDescription(bool shared) => shared
        ? "It's removed for you and every profile you share it with. This can't be undone."
        : "This can't be undone.";

    /// <summary>"1988 · Movie" for a title's row and search result.</summary>
    public static string TitleMeta(int? year, string type)
    {
        string kind = type switch
        {
            "movie" => "Movie",
            "series" => "Show",
            "season" => "Season",
            "episode" => "Episode",
            _ => "",
        };
        return string.Join(" · ", new[] { year?.ToString(System.Globalization.CultureInfo.InvariantCulture), kind }.Where(p => !string.IsNullOrEmpty(p)));
    }
}
