using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Granary.Core.ViewModels.Collections;

/// <summary>A title in the collection editor's list: its number, poster, name and "1988 · Movie", and its remove button.</summary>
public sealed partial class CollectionTitleRow : ObservableObject
{
    internal CollectionTitleRow(string id, string title, string meta, Uri? posterUrl, long position, ICommand removeCommand)
    {
        Id = id;
        Title = title;
        Meta = meta;
        PosterUrl = posterUrl;
        Position = position;
        RemoveCommand = removeCommand;
    }

    /// <summary>The title's content id.</summary>
    public string Id { get; }

    public string Title { get; }

    public string Meta { get; }

    public Uri? PosterUrl { get; }

    /// <summary>Where the server keeps it; a removed title goes back there on Undo.</summary>
    internal long Position { get; }

    /// <summary>Takes this row.</summary>
    public ICommand RemoveCommand { get; }

    /// <summary>1 for the first.</summary>
    [ObservableProperty]
    public partial int Number { get; internal set; }

    public string RemoveName => $"Remove {Title}";

    public string MoveName => $"Move {Title}";

    internal CollectionTitleRow At(long position) => new(Id, Title, Meta, PosterUrl, position, RemoveCommand);
}

/// <summary>A title the editor's search found: Add, or "In this collection" when it is there already.</summary>
public sealed partial class CollectionSearchResult : ObservableObject
{
    internal CollectionSearchResult(string id, string title, string meta, Uri? posterUrl, bool isIn, ICommand addCommand)
    {
        Id = id;
        Title = title;
        Meta = meta;
        PosterUrl = posterUrl;
        IsIn = isIn;
        AddCommand = addCommand;
    }

    public string Id { get; }

    public string Title { get; }

    public string Meta { get; }

    public Uri? PosterUrl { get; }

    /// <summary>Takes this result.</summary>
    public ICommand AddCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd), nameof(ActionText))]
    public partial bool IsIn { get; internal set; }

    public bool CanAdd => !IsIn;

    public string ActionText => IsIn ? "In this collection" : "Add";

    public string AccessibleName => $"{Title}, {Meta}";
}
