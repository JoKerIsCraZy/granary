using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Collections;

/// <summary>The collection editor's words, as the web's <c>lib/collections/copy.ts</c> has them.</summary>
public static class CollectionEditorText
{
    public static string NewCollection => AppText.Get("web.collections.copy.newCollection");

    public static string NotCreatedYet => AppText.Get("web.collections.copy.notCreatedYet");

    public static string TitlesAlreadySaved => AppText.Get("web.collections.copy.titlesAlreadySaved");

    public static string NameItThenCreate => AppText.Get("web.collections.copy.nameItThenCreate");

    public static string ConflictTitle => AppText.Get("web.collections.copy.conflictTitle");

    public static string Stale => AppText.Get("web.collections.scope.changedWhileEditing");

    /// <summary>"3 titles", "1 title".</summary>
    public static string Titles(int count) => AppText.Get("web.collections.metaLine.titleCount", ("count", count), ("shown", count));

    public static string TitlesReadyToAdd(int count) =>
        AppText.Get("web.collections.copy.titlesReadyToAdd", ("count", count), ("count_text", AppText.Number(count)));

    /// <summary>"Name and description not saved": the draft fields (<see cref="CollectionDraft.ChangedFrom"/>) by their labels.</summary>
    public static string NotSaved(IReadOnlyList<string> fields) => fields.Count == 0
        ? ""
        : AppText.Get("web.collections.copy.notSaved", ("fields", FieldNames(fields)));

    /// <summary>"When you save, Maya and Leo lose it, including rows they made from it."</summary>
    public static string UnshareWarning(IReadOnlyList<string> profiles) =>
        AppText.Get("web.collections.copy.unshareWarning", ("consequence", UnshareConsequence(profiles)));

    /// <summary>What turning sharing off costs: "Maya and Leo lose it, including rows they made from it."</summary>
    public static string UnshareConsequence(IReadOnlyList<string> profiles) => profiles.Count == 0
        ? AppText.Get("web.collections.copy.unshareConsequenceOthers")
        : AppText.Get("web.collections.copy.unshareConsequence", ("count", profiles.Count), ("names", TextList.And(profiles)));

    /// <summary>"Name and show only changed in both places."</summary>
    public static string ConflictFields(IReadOnlyList<string> fields) => fields.Count == 0
        ? AppText.Get("web.collections.editor.conflictSaveAgain")
        : AppText.Get("web.collections.editor.conflictFields", ("fields", FieldNames(fields)));

    /// <summary>The body of the delete question.</summary>
    public static string DeleteDescription(bool shared) => shared
        ? AppText.Get("web.collections.copy.personalDeleteDescriptionShared")
        : AppText.Get("web.collections.copy.cantBeUndone");

    /// <summary>"1988 · Movie" for a title's row and search result.</summary>
    public static string TitleMeta(int? year, string type)
    {
        string kind = type is "movie" or "series" or "season" or "episode"
            ? AppText.Get("web.collections.manual.typeLabel", ("type", type))
            : "";
        return string.Join(" · ", new[] { year?.ToString(System.Globalization.CultureInfo.InvariantCulture), kind }.Where(p => !string.IsNullOrEmpty(p)));
    }

    /// <summary>A draft field as the save bar and the conflict banner name it, as the web's <c>draftFieldLabel</c>.</summary>
    public static string FieldLabel(string field) => field switch
    {
        CollectionDraft.NameField => AppText.Get("web.collections.copy.fieldName"),
        CollectionDraft.DescriptionField => AppText.Get("web.collections.copy.fieldDescription"),
        CollectionDraft.LibrariesField => AppText.Get("web.collections.copy.fieldLibraries"),
        CollectionDraft.RulesField => AppText.Get("web.collections.copy.fieldRules"),
        CollectionDraft.OrderField => AppText.Get("web.collections.copy.fieldOrder"),
        CollectionDraft.ShowOnlyField => AppText.Get("web.collections.copy.fieldShowOnly"),
        CollectionDraft.SharedField => AppText.Get("web.collections.copy.showToOtherProfilesLabel"),
        CollectionDraft.CollectionsTabField => AppText.Get("web.collections.copy.showOnTabLabel"),
        CollectionDraft.PosterField => AppText.Get("web.collections.copy.slotPoster"),
        _ => field,
    };

    // "Name and description": the first field as labelled, the rest in lower case in English only, as the web's
    // lowerNames (other languages keep their labels as written; German nouns stay capitalized).
    private static string FieldNames(IReadOnlyList<string> fields)
    {
        var labels = fields.Select(FieldLabel).ToList();
        if (AppText.Language.Tag != AppLanguage.English)
        {
            return TextList.And(labels);
        }
        var culture = AppText.Language.Culture;
        return TextList.And([labels[0], .. labels.Skip(1).Select(label => label.Length == 0 ? label : char.ToLower(label[0], culture) + label[1..])]);
    }
}
