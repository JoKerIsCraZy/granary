using Microsoft.UI.Xaml;

namespace Granary.App.Controls;

/// <summary>
/// A page with more than one grid of cards (the Downloads page's films and series): a grid whose last card went with the
/// focus on it asks the page where the focus goes next.
/// </summary>
public interface ICardFocusHost
{
    /// <summary><paramref name="grid"/> has no card left and the focus went with its last one.</summary>
    void FocusAfterLastCard(MediaGrid grid, FocusState state);
}
