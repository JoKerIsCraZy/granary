using Granary.Core.Api.Catalog;

namespace Granary.Core.ViewModels.Collections;

/// <summary>
/// What the collection editor changes before Save, as the web's draft: name, description, Show only, and where it
/// shows. Titles are not part of it: they save as they are added, removed or moved.
/// </summary>
public sealed record CollectionDraft(string Name, string Description, CollectionShowOnly ShowOnly, bool IsShared, bool OnCollectionsTab)
{
    public static CollectionDraft Empty { get; } = new("", "", CollectionShowOnly.Everything, false, false);

    /// <summary>The fields in the order the save bar and the conflict banner name them.</summary>
    internal static IReadOnlyList<(string Label, Func<CollectionDraft, object> Value)> Fields { get; } =
    [
        ("Name", d => d.Name),
        ("Description", d => d.Description),
        ("Show only", d => d.ShowOnly),
        ("Show to other profiles", d => d.IsShared),
        ("Show on the Collections tab", d => d.OnCollectionsTab),
    ];

    public static CollectionDraft Of(PersonalCollection collection) => new(collection.Name, collection.Description,
        CollectionShowOnly.Read(collection.DisplayQueryDefinition), collection.IsShared, collection.IncludeInServerCollections);

    /// <summary>The labels of the fields that differ from <paramref name="other"/>, in the save bar's order.</summary>
    public IReadOnlyList<string> ChangedFrom(CollectionDraft other) =>
        [.. Fields.Where(f => !Equals(f.Value(this), f.Value(other))).Select(f => f.Label)];

    public PersonalCollectionUpdate ToUpdate() => new()
    {
        Name = Name.Trim(),
        Description = Description.Trim(),
        IsShared = IsShared,
        IncludeInServerCollections = OnCollectionsTab,
        DisplayQueryDefinition = ShowOnly.ToQuery(),
    };

    public PersonalCollectionCreate ToCreate() => new()
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
        var conflicts = Fields
            .Where(f => !Equals(f.Value(mine), f.Value(original)) && !Equals(f.Value(theirs), f.Value(original)) && !Equals(f.Value(mine), f.Value(theirs)))
            .Select(f => f.Label)
            .ToList();
        T Pick<T>(Func<CollectionDraft, T> field) where T : notnull =>
            Equals(field(mine), field(original)) ? field(theirs) : field(mine);
        var merged = new CollectionDraft(Pick(d => d.Name), Pick(d => d.Description), Pick(d => d.ShowOnly), Pick(d => d.IsShared),
            Pick(d => d.OnCollectionsTab));
        return (merged, conflicts);
    }
}
