namespace Granary.App.Views;

/// <summary>
/// A press (a click, Enter, a remote's OK) on a sidebar entry while the settings' search narrows the pages: focus goes to the
/// page's match, at once on the page that shows, else once its page opens. A press on the open page never waits, so one made
/// while the mpv.conf editor stands in its place is not left over to pull focus into a page the arrows open later. A press
/// on another entry waits for the opening its choice brings, also while the mpv.conf question holds it back; a declined
/// question forgets it, as a new query does. No XAML here, so it is tested alone.
/// </summary>
internal sealed class SettingsSearchPress
{
    private (string Page, bool ByKeyboard)? _waiting;

    /// <param name="openPage">The page that shows, or that the mpv.conf editor stands in for.</param>
    /// <param name="showsOpenPage">The open page's view shows, not the editor.</param>
    /// <returns>True when the pressed page shows: its match takes focus now.</returns>
    public bool Press(string page, string openPage, bool showsOpenPage, bool byKeyboard)
    {
        _waiting = page != openPage ? (page, byKeyboard) : null;
        return page == openPage && showsOpenPage;
    }

    /// <summary>Whether a press waits for <paramref name="page"/>, which has just opened; any press waiting is used up.</summary>
    public bool TryTake(string page, out bool byKeyboard)
    {
        var waiting = _waiting;
        _waiting = null;
        byKeyboard = waiting?.ByKeyboard ?? false;
        return waiting?.Page == page;
    }

    /// <summary>A new query or a choice the viewer took back: the press waiting goes.</summary>
    public void Forget() => _waiting = null;
}
