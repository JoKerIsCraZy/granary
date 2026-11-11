using Granary.Core.Playback;

namespace Granary.Core.ViewModels.Player;

/// <summary>
/// Where Ctrl+Right and Ctrl+Left go, as ⌘→ and ⌘← on Silo's macOS app (<c>seekToAdjacentChapter</c>): to the start of the
/// first chapter more than a second ahead, or of the last one more than a second behind. So back goes to the start of the
/// chapter that plays, and within its first second on to the one before.
/// </summary>
public static class ChapterJump
{
    // Apple's margin: a start this close to the position counts as where it is.
    private const double MarginSeconds = 1;

    /// <summary>The start to seek to from <paramref name="seconds"/>; null past the first or last chapter, and without chapters.</summary>
    public static double? Target(IReadOnlyList<PlaybackChapter> chapters, double seconds, bool forward)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        // The nearest start past the margin that way; the chapters need not be in order.
        double? target = null;
        foreach (var chapter in chapters)
        {
            double start = chapter.StartSeconds;
            bool isPastMargin = forward ? start > seconds + MarginSeconds : start < seconds - MarginSeconds;
            bool isNearer = target is not { } nearest || (forward ? start < nearest : start > nearest);
            if (isPastMargin && isNearer)
            {
                target = start;
            }
        }
        return target;
    }
}
