namespace Granary.App.Views;

/// <summary>
/// A press (a click, Enter, a remote's OK) on a sidebar entry while the settings' search narrows the pages: focus goes to the
/// page's match, at once on the page that shows, else once the choice the press makes has opened it. A press waits only when
/// a choice will follow; one that would wait for nothing (its entry already chosen, the mpv.conf editor in its page's place)
/// is let go, so none is left over to pull focus into a page the arrows open later. No XAML here, so it is tested alone.
/// </summary>
internal sealed class SettingsSearchPress
{
    private (string Page, bool ByKeyboard)? _waiting;

    /// <param name="openPage">The page that shows, or that the mpv.conf editor stands in for.</param>
    /// <param name="showsOpenPage">The open page's view shows, not the editor.</param>
    /// <param name="isChosen">The entry is chosen already, so the press makes no choice.</param>
    /// <returns>True when the pressed page shows: its match takes focus now.</returns>
    public bool Press(string page, string openPage, bool showsOpenPage, bool isChosen, bool byKeyboard)
    {
        _waiting = page != openPage && !isChosen ? (page, byKeyboard) : null;
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
