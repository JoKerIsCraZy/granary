using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Player;

namespace Granary.Core.ViewModels;

// The chapters of the playing file (docs/api-contract.md, 11.3): the web player's Chapters menu, and the ticks on its
// seek bar.
public sealed partial class PlayerViewModel
{
    private int _currentChapter = -1;

    /// <summary>The chapters in source seconds, for the seek bar.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PlaybackChapter> ChapterMarks { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ChapterOption> Chapters { get; private set; } = [];

    [ObservableProperty]
    public partial bool HasChapters { get; private set; }

    /// <summary>A row of the Chapters menu: to where the chapter starts.</summary>
    public void SeekToChapter(double startSeconds) => SeekTo(startSeconds);

    private void ResetChapters()
    {
        _currentChapter = -1;
        ChapterMarks = [];
        Chapters = [];
        HasChapters = false;
    }

    // A plan can play another file, with chapters of its own.
    private void TakeChapters(IServerPlayback playback)
    {
        var chapters = playback.Chapters;
        if (chapters.SequenceEqual(ChapterMarks))
        {
            return;
        }
        ChapterMarks = chapters;
        HasChapters = chapters.Count > 0;
        ShowChapters(ChapterOption.IndexAt(chapters, PositionSeconds));
    }

    // The rows change only when another chapter plays, so an open menu keeps its rows while the time runs.
    private void FollowChapters()
    {
        int current = ChapterOption.IndexAt(ChapterMarks, PositionSeconds);
        if (current != _currentChapter)
        {
            ShowChapters(current);
        }
    }

    private void ShowChapters(int current)
    {
        _currentChapter = current;
        Chapters = ChapterOption.For(ChapterMarks, current);
    }
}
