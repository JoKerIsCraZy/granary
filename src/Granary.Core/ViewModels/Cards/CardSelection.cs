using CommunityToolkit.Mvvm.ComponentModel;

namespace Granary.Core.ViewModels.Cards;

/// <summary>
/// The cards a grid has picked while it selects (the web's History "Select"): a card's click picks or drops it instead of
/// opening its title. Picks are kept by id, with the card, so a pick still counts after its card scrolled away.
/// </summary>
public sealed partial class CardSelection : ObservableObject
{
    private readonly Dictionary<string, MediaCard> _picked = new(StringComparer.Ordinal);

    /// <summary>The grid selects: cards show their pick mark, and a click picks.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicks), nameof(CountText))]
    public partial int Count { get; private set; }

    public bool HasPicks => Count > 0;

    /// <summary>"3 selected".</summary>
    public string CountText => $"{Count} selected";

    /// <summary>The picked cards, in the order they were picked.</summary>
    public IReadOnlyList<MediaCard> Cards => [.. _picked.Values];

    /// <summary>Raised when the picks or <see cref="IsActive"/> changed: the grid redraws its marks.</summary>
    public event EventHandler? Changed;

    public bool Contains(string id) => _picked.ContainsKey(id);

    public void Start()
    {
        IsActive = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Done: the picks go with the selecting.</summary>
    public void Stop()
    {
        IsActive = false;
        Clear();
    }

    /// <summary>Picks <paramref name="card"/>, or drops it when it was picked. A placeholder is not a title to pick.</summary>
    public void Toggle(MediaCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!IsActive || card.IsPlaceholder)
        {
            return;
        }
        if (!_picked.Remove(card.Id))
        {
            _picked[card.Id] = card;
        }
        Show();
    }

    /// <summary>Picks every card of <paramref name="cards"/> that is a title.</summary>
    public void PickAll(IEnumerable<MediaCard> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        foreach (var card in cards.Where(c => !c.IsPlaceholder))
        {
            _picked.TryAdd(card.Id, card);
        }
        Show();
    }

    public void Clear()
    {
        _picked.Clear();
        Show();
    }

    private void Show()
    {
        Count = _picked.Count;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
