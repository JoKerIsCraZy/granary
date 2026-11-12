using System.Windows.Input;

namespace Granary.Core.ViewModels.Cards;

/// <summary>What a click on a card's button does, for the rows and grids that hold cards.</summary>
public static class CardClick
{
    /// <summary>
    /// Nothing more when the click already opened the card's heading (<paramref name="openedHeading"/>, from
    /// <see cref="HeadingPress.TryOpen"/>). Else, while the grid selects, the click picks the card or drops it; else it runs
    /// the page's command with the card.
    /// </summary>
    public static void Run(MediaCard card, bool openedHeading, ICommand? invoked, CardSelection? selection = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (openedHeading)
        {
            return;
        }
        if (selection is { IsActive: true })
        {
            selection.Toggle(card);
            return;
        }
        if (invoked is not null && invoked.CanExecute(card))
        {
            invoked.Execute(card);
        }
    }
}
