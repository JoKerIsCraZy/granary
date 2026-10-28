namespace Granary.App.Views;

/// <summary>
/// A page with a sidebar of its own (Settings, Admin): it runs to the window's foot beside the minimized player's bar, so
/// the window does not end the whole page above the bar; the page ends only its own content there.
/// </summary>
internal interface IDockSpace
{
    /// <summary>The height the bar takes at the window's foot; 0 while there is none.</summary>
    void ReserveBottom(double height);
}
