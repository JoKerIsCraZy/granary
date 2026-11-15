using Granary.Core.ViewModels.Cards;

namespace Granary.App.Controls;

/// <summary>
/// A grid or a page whose cards carry entries of its list's own in their ⋯ menu: a collection's Edit. A card asks the nearest
/// one above it, except in a <see cref="MediaRow"/>, which has its own Remove. Download is every card's own
/// (<c>CardDownloads</c>), not a list's.
/// </summary>
public interface ICardMenuHost
{
    /// <summary>The list that adds the entries; null adds none.</summary>
    ICardMenuSource? CardMenu { get; }
}
