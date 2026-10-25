using System.Collections.ObjectModel;
using System.Windows.Input;
using Granary.Core.Api.Catalog;
using Granary.Core.ViewModels.Cards;

namespace Granary.Core.ViewModels;

/// <summary>One Home row: its title, its cards, and "Explore all" when the server has more than it sent.</summary>
public sealed class HomeRowViewModel
{
    private readonly Dictionary<MediaCard, CatalogItem> _items = [];

    internal HomeRowViewModel(Section section, IEnumerable<(MediaCard Card, CatalogItem Item)> cards, ICommand openCard, ICommand? exploreAll)
    {
        SectionId = section.Id;
        SectionType = section.SectionType;
        Title = section.Title;
        OpenCardCommand = openCard;
        ExploreAllCommand = exploreAll;
        foreach (var (card, item) in cards)
        {
            if (_items.TryAdd(card, item))
            {
                Cards.Add(card);
            }
        }
    }

    public string SectionId { get; }

    public string SectionType { get; }

    public string Title { get; }

    public ObservableCollection<MediaCard> Cards { get; } = [];

    /// <summary>Opens a card; the parameter is the <see cref="MediaCard"/>.</summary>
    public ICommand OpenCardCommand { get; }

    /// <summary>Null when the row already shows every card it has.</summary>
    public ICommand? ExploreAllCommand { get; }

    /// <summary>Continue Watching and Next Up cards can be removed from Home.</summary>
    public bool CanRemoveCards => HomeRowRules.IsWatchingRow(SectionType);

    internal CatalogItem? ItemOf(MediaCard card) => _items.GetValueOrDefault(card);

    /// <summary>
    /// Takes the cards of <paramref name="fresh"/>, the same row loaded again, in place: a card of a title already shown
    /// is replaced where it is, the others come and go, so the row keeps where it was scrolled to.
    /// </summary>
    internal void Take(HomeRowViewModel fresh)
    {
        var next = fresh.Cards;
        var ids = next.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        for (int i = Cards.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(Cards[i].Id))
            {
                Cards.RemoveAt(i);
            }
        }
        for (int i = 0; i < next.Count; i++)
        {
            int at = IndexOf(next[i].Id, i);
            if (at < 0)
            {
                Cards.Insert(i, next[i]);
                continue;
            }
            if (at != i)
            {
                Cards.Move(at, i);
            }
            if (!Equals(Cards[i], next[i]))
            {
                Cards[i] = next[i];
            }
        }
        while (Cards.Count > next.Count)
        {
            Cards.RemoveAt(Cards.Count - 1);
        }
        _items.Clear();
        foreach (var (card, item) in fresh._items)
        {
            _items[card] = item;
        }
    }

    private int IndexOf(string id, int from)
    {
        for (int i = from; i < Cards.Count; i++)
        {
            if (Cards[i].Id == id)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Removes the cards whose item <paramref name="matches"/>; returns whether any went.</summary>
    internal bool RemoveWhere(Func<CatalogItem, bool> matches)
    {
        var gone = _items.Where(pair => matches(pair.Value)).Select(pair => pair.Key).ToList();
        foreach (var card in gone)
        {
            _items.Remove(card);
            Cards.Remove(card);
        }
        return gone.Count > 0;
    }
}

/// <summary>How Home rows and their "Explore all" grids treat row types (docs/api-contract.md, 7.1).</summary>
internal static class HomeRowRules
{
    /// <summary>Continue Watching and Next Up: landscape cards with progress, removable.</summary>
    public static bool IsWatchingRow(string? sectionType) => sectionType is SectionTypes.ContinueWatching or SectionTypes.NextUp;

    /// <summary>
    /// The same row type backs Continue Listening and Reading, so a Continue Watching row keeps only
    /// movies, episodes and series.
    /// </summary>
    public static bool Shows(string? sectionType, CatalogItemBase item) =>
        sectionType != SectionTypes.ContinueWatching
        || item.Type is CatalogItemTypes.Movie or CatalogItemTypes.Episode or CatalogItemTypes.Series;
}
