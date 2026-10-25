using System.Globalization;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels.Player;

/// <summary>A row of the web player's Chapters menu: the picture, the title and where it starts; the one that plays is marked.</summary>
public sealed record ChapterOption(string Title, double StartSeconds, Uri? Thumbnail, bool IsCurrent)
{
    /// <summary>The rows of <paramref name="chapters"/>, the one at <paramref name="current"/> marked.</summary>
    public static IReadOnlyList<ChapterOption> For(IReadOnlyList<PlaybackChapter> chapters, int current)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        return
        [
            .. chapters.Select((chapter, index) => new ChapterOption(
                chapter.Title.Trim() is { Length: > 0 } title ? title : string.Create(CultureInfo.InvariantCulture, $"Chapter {index + 1}"),
                chapter.StartSeconds, chapter.ThumbnailUrl, index == current)),
        ];
    }

    /// <summary>The chapter that holds <paramref name="seconds"/>; -1 before the first and in a gap.</summary>
    public static int IndexAt(IReadOnlyList<PlaybackChapter> chapters, double seconds)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        for (int index = 0; index < chapters.Count; index++)
        {
            if (chapters[index].Contains(seconds))
            {
                return index;
            }
        }
        return -1;
    }
}
