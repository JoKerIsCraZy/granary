namespace Granary.App.Views;

/// <summary>
/// A page whose sections are no row of tabs or pills (Settings' and Admin's own sidebar, Search's Movies and Series, the
/// Downloads page's Films and Series, a series' downloads by season): a game controller's bumpers step through them all the
/// same (MainWindow.Controller.Pages.cs, StepPadTab).
/// </summary>
internal interface IPadSections
{
    /// <summary>
    /// Shows the section before (-1) or after (1) the shown one, as picking it does, with focus on its entry so the viewer
    /// sees where they are; where every section shows at once, focus goes to the section's first card. Nothing at the ends.
    /// </summary>
    void StepSection(int by);
}
