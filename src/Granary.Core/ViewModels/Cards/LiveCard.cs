using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Granary.Core.ViewModels.Cards;

/// <summary>
/// A card whose look follows something that changes while it shows, as a download's progress (the Downloads page). A grid
/// shows <see cref="Card"/> and follows it in place: the card keeps its place, its focus and its artwork while its lines and
/// bar change. A list of plain <see cref="MediaCard"/>s would have to replace the card, which takes the focus off it.
/// </summary>
public sealed partial class LiveCard(MediaCard card) : ObservableObject
{
    /// <summary>The card as it is now; a change is told only when it differs (<see cref="MediaCard"/> compares by value).</summary>
    [ObservableProperty]
    public partial MediaCard Card { get; set; } = card;

    /// <summary>
    /// Brings <paramref name="shown"/> in line with <paramref name="wanted"/>, in place and by <see cref="MediaCard.Id"/>: a card
    /// that stays keeps its <see cref="LiveCard"/>, and with it its element in the grid and the focus; one that went is
    /// removed, one that came is inserted where it belongs, and every card that stays takes its new look.
    /// </summary>
    public static void Sync(ObservableCollection<LiveCard> shown, IReadOnlyList<MediaCard> wanted)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(wanted);
        var ids = wanted.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        for (int i = shown.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(shown[i].Card.Id))
            {
                shown.RemoveAt(i);
            }
        }
        for (int i = 0; i < wanted.Count; i++)
        {
            int at = IndexOf(shown, wanted[i].Id, i);
            if (at < 0)
            {
                shown.Insert(i, new LiveCard(wanted[i]));
                continue;
            }
            if (at != i)
            {
                shown.Move(at, i);
            }
            shown[i].Card = wanted[i];
        }
    }

    private static int IndexOf(ObservableCollection<LiveCard> shown, string id, int from)
    {
        for (int i = from; i < shown.Count; i++)
        {
            if (string.Equals(shown[i].Card.Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }
}
