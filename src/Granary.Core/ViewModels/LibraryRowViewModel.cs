using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.ViewModels.Cards;

namespace Granary.Core.ViewModels;

/// <summary>One row of a library's Recommended tab. It stays hidden until it has cards.</summary>
public sealed partial class LibraryRowViewModel(string id, string title, ICommand openCard, ICommand exploreAll) : ObservableObject
{
    /// <summary>The server's section id; never key logic on it.</summary>
    public string Id { get; } = id;

    public string Title { get; } = title;

    /// <summary>Takes the <see cref="MediaCard"/> to open.</summary>
    public ICommand OpenCardCommand { get; } = openCard;

    public ICommand ExploreAllCommand { get; } = exploreAll;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCards))]
    public partial IReadOnlyList<MediaCard> Cards { get; internal set; } = [];

    public bool HasCards => Cards.Count > 0;
}
