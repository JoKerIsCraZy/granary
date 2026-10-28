using System.Windows.Input;

namespace Granary.Core.ViewModels.Cards;

/// <summary>
/// An entry a list adds to its cards' ⋯ menu: "Edit collection", "Delete…". The command takes the card;
/// <see cref="IsChecked"/> set makes it a check item ("Show to other profiles").
/// </summary>
public sealed record CardMenuEntry(string Label, ICommand Command, bool? IsChecked = null);

/// <summary>A list whose cards carry entries of its own in their ⋯ menu.</summary>
public interface ICardMenuSource
{
    /// <summary>The entries for <paramref name="card"/>; none for a card the list adds nothing to.</summary>
    IReadOnlyList<CardMenuEntry> EntriesFor(MediaCard card);
}
