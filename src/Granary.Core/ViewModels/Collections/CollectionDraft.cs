using System.Text.Json;
using Granary.Core.Api.Catalog;

namespace Granary.Core.ViewModels.Collections;

/// <summary>
/// What the collection editor changes before Save, as the web's draft: name, description, a manual collection's Show
/// only, a smart collection's libraries, rules and saved sort, and where it shows. A manual collection's titles are not
/// part of it: they save as they are added, removed or moved.
/// </summary>
/// <param name="Kind"><see cref="PersonalCollectionTypes.Manual"/> or <see cref="PersonalCollectionTypes.Smart"/>.</param>
/// <param name="Rules">A smart collection's rules, with its libraries; null for a manual one.</param>
/// <param name="SortConfig">A smart collection's <c>sort_config</c> as JSON with its keys in order.</param>
public sealed record CollectionDraft(string Name, string Description, CollectionShowOnly ShowOnly, bool IsShared, bool OnCollectionsTab,
    string Kind = PersonalCollectionTypes.Manual, SmartQuery? Rules = null, string SortConfig = "{}")
{
    public static CollectionDraft Empty { get; } = new("", "", CollectionShowOnly.Everything, false, false);

    /// <summary>A new smart collection: every title of every library, newest first.</summary>
    public static CollectionDraft EmptySmart { get; } = Empty with { Kind = PersonalCollectionTypes.Smart, Rules = SmartQuery.Empty };

    public bool IsSmart => Kind == PersonalCollectionTypes.Smart;

    /// <summary>The fields in the order the save bar and the conflict banner name them, read for comparing and set from another draft.</summary>
    private static readonly IReadOnlyList<(string Label, Func<CollectionDraft, object> Get, Func<CollectionDraft, CollectionDraft, CollectionDraft> Take)> Fields =
    [
        ("Name", d => d.Name, (d, from) => d with { Name = from.Name }),
        ("Description", d => d.Description, (d, from) => d with { Description = from.Description }),
        ("Libraries", d => d.Rules?.LibrariesKey() ?? "",
            (d, from) => d.Rules is null ? d : d with { Rules = d.Rules with { LibraryIds = from.Rules?.LibraryIds ?? [] } }),
        // The rules' libraries count once, as Libraries.
        ("Rules", d => d.Rules?.RulesKey() ?? "",
            (d, from) => d.Rules is null || from.Rules is null ? d : d with { Rules = from.Rules with { LibraryIds = d.Rules.LibraryIds } }),
        ("Order", d => d.SortConfig, (d, from) => d with { SortConfig = from.SortConfig }),
        ("Show only", d => d.ShowOnly, (d, from) => d with { ShowOnly = from.ShowOnly }),
        ("Show to other profiles", d => d.IsShared, (d, from) => d with { IsShared = from.IsShared }),
        ("Show on the Collections tab", d => d.OnCollectionsTab, (d, from) => d with { OnCollectionsTab = from.OnCollectionsTab }),
    ];

    public static CollectionDraft Of(PersonalCollection collection) => collection.IsSmart
        ? new(collection.Name, collection.Description, CollectionShowOnly.Everything, collection.IsShared, collection.IncludeInServerCollections,
            PersonalCollectionTypes.Smart, SmartQuery.Read(collection.QueryDefinition), SmartSortConfig.Key(SmartSortConfig.Of(collection.SortConfig)))
        : new(collection.Name, collection.Description, CollectionShowOnly.Read(collection.DisplayQueryDefinition), collection.IsShared,
            collection.IncludeInServerCollections);

    /// <summary>The labels of the fields that differ from <paramref name="other"/>, in the save bar's order.</summary>
    public IReadOnlyList<string> ChangedFrom(CollectionDraft other) =>
        [.. Fields.Where(f => !Equals(f.Get(this), f.Get(other))).Select(f => f.Label)];

    /// <summary>The same in every field the editor saves (record equality would compare the rules' lists by reference).</summary>
    public bool SameAs(CollectionDraft other) => Kind == other.Kind && ChangedFrom(other).Count == 0;

    /// <summary>This draft with the fields named by <paramref name="labels"/> taken from <paramref name="from"/>.</summary>
    public CollectionDraft Taking(CollectionDraft from, IEnumerable<string> labels) =>
        labels.Aggregate(this, (draft, label) => Fields.FirstOrDefault(f => f.Label == label) is { Take: { } take } ? take(draft, from) : draft);

    public PersonalCollectionUpdate ToUpdate() => IsSmart
        ? new()
        {
            Name = Name.Trim(),
            Description = Description.Trim(),
            IsShared = IsShared,
            IncludeInServerCollections = OnCollectionsTab,
            QueryDefinition = Rules ?? SmartQuery.Empty,
            SortConfig = SortConfigElement(),
        }
        : new()
        {
            Name = Name.Trim(),
            Description = Description.Trim(),
            IsShared = IsShared,
            IncludeInServerCollections = OnCollectionsTab,
            DisplayQueryDefinition = ShowOnly.ToQuery(),
        };

    public PersonalCollectionCreate ToCreate() => IsSmart
        ? new()
        {
            Name = Name.Trim(),
            Description = Description.Trim(),
            IsShared = IsShared,
            IncludeInServerCollections = OnCollectionsTab,
            CollectionType = PersonalCollectionTypes.Smart,
            QueryDefinition = Rules ?? SmartQuery.Empty,
            SortConfig = SortConfigElement(),
        }
        : new()
        {
            Name = Name.Trim(),
            Description = Description.Trim(),
            IsShared = IsShared,
            IncludeInServerCollections = OnCollectionsTab,
            DisplayQueryDefinition = ShowOnly.IsEverything ? null : ShowOnly.ToQuery(),
        };

    /// <summary>
    /// A three-way merge after the collection changed elsewhere: a field changed only here keeps this side's value, one
    /// changed only there takes theirs, and one changed on both sides to different values is a conflict, kept as mine
    /// until the viewer picks.
    /// </summary>
    public static (CollectionDraft Merged, IReadOnlyList<string> Conflicts) Merge(CollectionDraft original, CollectionDraft mine, CollectionDraft theirs)
    {
        var conflicts = new List<string>();
        var merged = theirs;
        foreach (var (label, get, take) in Fields)
        {
            object was = get(original);
            object ours = get(mine);
            object server = get(theirs);
            if (Equals(ours, was) || Equals(ours, server))
            {
                continue;
            }
            if (!Equals(server, was))
            {
                conflicts.Add(label);
            }
            merged = take(merged, mine);
        }
        return (merged, conflicts);
    }

    private JsonElement SortConfigElement()
    {
        using var document = JsonDocument.Parse(SortConfig);
        return document.RootElement.Clone();
    }
}
