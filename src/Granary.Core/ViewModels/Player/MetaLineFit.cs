namespace Granary.Core.ViewModels.Player;

/// <summary>
/// How the player's line under the title shares its width (the App's <c>PlayerMetaRow</c>): the times and the end clock
/// always whole, then the episode, then the chapter in what is left. As the web's <c>TitleBlock</c>, the chapter is cut
/// before the episode, and the episode only once there is no room for a chapter at all.
/// </summary>
public static class MetaLineFit
{
    /// <summary>Below this, in pixels, a cut chapter would say nothing, so it is left out.</summary>
    public const double MinChapterWidth = 48;

    /// <param name="episode">The episode part's natural width, its dot included.</param>
    /// <param name="chapter">The chapter part's natural width, its dot included; 0 without one.</param>
    /// <param name="others">The parts that never give way: the times and the end clock.</param>
    /// <param name="room">The width the line has.</param>
    /// <returns>The widths the episode and the chapter get.</returns>
    public static (double Episode, double Chapter) Split(double episode, double chapter, double others, double room)
    {
        double left = Math.Max(0, room - others);
        double episodeWidth = Math.Min(episode, left);
        double chapterWidth = Math.Min(chapter, left - episodeWidth);
        return (episodeWidth, chapterWidth < MinChapterWidth && chapterWidth < chapter ? 0 : chapterWidth);
    }
}
