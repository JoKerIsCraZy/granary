namespace Granary.Core.ViewModels.Cards;

/// <summary>
/// A pointer's press that began on a card's title line while that line opens something other than the card, an episode's
/// series (<see cref="CardLinks.OpensHeading"/>), kept until the click it ends in. The button around the card presses and
/// clicks as for any press; the click then opens the series instead of the card (<see cref="CardClick"/>). A press that
/// ends in no click is forgotten (<see cref="Clear"/>): when the pointer leaves or is cancelled, and before a key clicks
/// the button, so Enter or a remote's Select never opens the series by a press left over.
/// </summary>
public sealed class HeadingPress
{
    private MediaCard? _card;

    /// <summary>A press waits for its click.</summary>
    public bool IsPending => _card is not null;

    /// <summary>
    /// A press began on <paramref name="line"/> of <paramref name="card"/>; <see cref="CardLines.None"/> for one on the
    /// artwork or with another button than the left. Every press forgets the one before it.
    /// </summary>
    public void Begin(MediaCard? card, CardLines line, bool isSelecting = false) =>
        _card = CardLinks.OpensHeading(card, line, isSelecting) ? card : null;

    /// <summary>The press ended without a click, or something other than the pointer is about to click.</summary>
    public void Clear() => _card = null;

    /// <summary>
    /// A click on <paramref name="card"/>: when it ends a press on that card's title line, opens the series and says so.
    /// The press is used up either way. Not for another card than the press began on (a container given another title in
    /// between), and not once the card's grid selects.
    /// </summary>
    public bool TryOpen(MediaCard? card, MediaCardActions actions, bool isSelecting = false)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var pressed = _card;
        _card = null;
        return pressed is not null && pressed.Equals(card) && CardLinks.OpensHeading(card, CardLines.Title, isSelecting)
            && actions.OpenHeading(pressed);
    }
}
