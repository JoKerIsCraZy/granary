using Granary.Core.ViewModels.Cards;

namespace Granary.App.Controls;

/// <summary>
/// A grid or a page whose cards carry entries of its list's own in their ⋯ menu: a collection's Edit, a season's episodes'
/// Download. A card asks the nearest one above it, except in a <see cref="MediaRow"/>, which has its own Remove.
/// </summary>
public interface ICardMenuHost
{
    /// <summary>The list that adds the entries; null adds none.</summary>
    ICardMenuSource? CardMenu { get; }
}
