namespace Granary.Core.Navigation;

/// <summary>Where keyboard focus is when Back comes: in the sidebar, in the page, or nowhere.</summary>
public enum FocusArea
{
    None,
    Sidebar,
    Content,
}

/// <summary>A key that means Back from a remote or keyboard.</summary>
public enum BackKey
{
    Escape,
    Backspace,

    /// <summary>The Back button of most PC remotes.</summary>
    BrowserBack,
}

/// <summary>What the window knows when Back comes, outside the player.</summary>
/// <param name="IsAtSection">The page is a sidebar section's own page, not one opened within it.</param>
public readonly record struct BackContext(FocusArea Focus, bool IsAtSection, bool IsHome, bool CanGoBack);

public enum BackStep
{
    Nothing,

    /// <summary>The page that was shown before comes back.</summary>
    GoBack,

    /// <summary>Focus moves to the section's entry in the sidebar.</summary>
    FocusSidebar,

    /// <summary>Home shows, and the sidebar keeps focus, on Home's entry.</summary>
    GoHomeInSidebar,
}

/// <summary>
/// What Back does outside a menu or dialog (which close first, MainWindow.Remote.cs), as Silo's TV app
/// (<c>TvShellFocusState</c>): a page opened within a section goes back to the page it came from. A section's own page
/// first moves focus to its entry in the sidebar; from there Back goes Home, and on Home it does nothing: the app does
/// not quit.
/// </summary>
public static class BackLadder
{
    public static BackStep Decide(BackContext context) => context switch
    {
        { IsAtSection: false, CanGoBack: true } => BackStep.GoBack,
        { IsAtSection: false } => BackStep.Nothing,
        { Focus: FocusArea.Content } => BackStep.FocusSidebar,
        { IsHome: true } => BackStep.Nothing,
        { CanGoBack: true } => BackStep.GoHomeInSidebar,
        _ => BackStep.Nothing,
    };

    /// <summary>Whether a key means Back: Backspace only while nothing is being typed.</summary>
    public static bool IsBack(BackKey key, bool isTyping) => key != BackKey.Backspace || !isTyping;
}
