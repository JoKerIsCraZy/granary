using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Granary.Core.ViewModels.Cards;

namespace Granary.Tests.ViewModels;

/// <summary>
/// A list of live cards is brought in line in place: a card that stays keeps its holder (its element in the grid, the focus),
/// one that went goes, one that came comes where it belongs, and an unchanged card tells nothing.
/// </summary>
public sealed class LiveCardTests
{
    private static MediaCard Card(string id, string caption = "Queued") => new(id, "download", "Title " + id) { Caption = caption };

    [Fact]
    public void Sync_KeepsTheCardsThatStay_InTheirNewOrder()
    {
        var shown = new ObservableCollection<LiveCard>();
        LiveCard.Sync(shown, [Card("a"), Card("b"), Card("c")]);
        var (a, c) = (shown[0], shown[2]);

        LiveCard.Sync(shown, [Card("c"), Card("d"), Card("a", "Paused")]);

        Assert.Equal(["c", "d", "a"], shown.Select(s => s.Card.Id));
        Assert.Same(c, shown[0]);
        Assert.Same(a, shown[2]);
        Assert.Equal("Paused", a.Card.Caption);
    }

    [Fact]
    public void Sync_ChangesNothing_WhenTheCardsAreAsShown()
    {
        var shown = new ObservableCollection<LiveCard>();
        LiveCard.Sync(shown, [Card("a"), Card("b")]);
        int listChanges = 0;
        int cardChanges = 0;
        shown.CollectionChanged += (_, _) => listChanges++;
        foreach (var live in shown)
        {
            live.PropertyChanged += (_, _) => cardChanges++;
        }

        // Equal cards, made anew: a card compares by value.
        LiveCard.Sync(shown, [Card("a"), Card("b")]);

        Assert.Equal((0, 0), (listChanges, cardChanges));
    }

    [Fact]
    public void Sync_RemovesAndInserts_RatherThanResetting()
    {
        var shown = new ObservableCollection<LiveCard>();
        LiveCard.Sync(shown, [Card("a"), Card("b")]);
        var actions = new List<NotifyCollectionChangedAction>();
        shown.CollectionChanged += (_, e) => actions.Add(e.Action);

        LiveCard.Sync(shown, [Card("b"), Card("c")]);

        Assert.Equal([NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add], actions);
    }
}
