using Granary.Core.Playback;
using Granary.Core.ViewModels.Player;

namespace Granary.Tests.ViewModels;

/// <summary>Where Ctrl+Right and Ctrl+Left go, as ⌘→ and ⌘← on Silo's macOS app.</summary>
public sealed class ChapterJumpTests
{
    private static readonly PlaybackChapter[] Chapters =
    [
        new("Opening", 0, 300, null),
        new("Chase", 300, 900, null),
        new("Heist", 1000, 1800, null),
    ];

    [Theory]
    [InlineData(30, 300)]
    [InlineData(300, 1000)]
    [InlineData(950, 1000)]
    [InlineData(298.9, 300)]
    public void Forward_GoesToTheNextChaptersStart(double seconds, double expected) =>
        Assert.Equal(expected, ChapterJump.Target(Chapters, seconds, forward: true));

    [Theory]
    [InlineData(299.5)]
    [InlineData(299)]
    public void Forward_PassesAChapterThatStartsWithinASecond(double seconds) =>
        Assert.Equal(1000, ChapterJump.Target(Chapters, seconds, forward: true));

    [Theory]
    [InlineData(1200)]
    [InlineData(999.5)]
    [InlineData(1800)]
    public void Forward_FromTheLastChapter_GoesNowhere(double seconds) =>
        Assert.Null(ChapterJump.Target(Chapters, seconds, forward: true));

    [Theory]
    [InlineData(305, 300)]
    [InlineData(301.5, 300)]
    [InlineData(1200, 1000)]
    [InlineData(30, 0)]
    public void Back_MoreThanASecondIntoAChapter_GoesToItsStart(double seconds, double expected) =>
        Assert.Equal(expected, ChapterJump.Target(Chapters, seconds, forward: false));

    [Theory]
    [InlineData(300, 0)]
    [InlineData(300.5, 0)]
    [InlineData(301, 0)]
    [InlineData(1000.2, 300)]
    public void Back_WithinASecondOfAChaptersStart_GoesToTheOneBefore(double seconds, double expected) =>
        Assert.Equal(expected, ChapterJump.Target(Chapters, seconds, forward: false));

    [Fact]
    public void Back_InAGapBetweenChapters_GoesToTheStartOfTheOneBefore() =>
        Assert.Equal(300, ChapterJump.Target(Chapters, 950, forward: false));

    [Theory]
    [InlineData(0)]
    [InlineData(0.8)]
    [InlineData(1)]
    public void Back_InTheFirstSecondOfTheFirstChapter_GoesNowhere(double seconds) =>
        Assert.Null(ChapterJump.Target(Chapters, seconds, forward: false));

    [Fact]
    public void BeforeTheFirstChapter_ForwardGoesToIt_AndBackGoesNowhere()
    {
        PlaybackChapter[] chapters = [new("Story", 5, 600, null), new("Credits", 600, 700, null)];

        Assert.Equal(5, ChapterJump.Target(chapters, 2, forward: true));
        Assert.Null(ChapterJump.Target(chapters, 2, forward: false));
    }

    [Fact]
    public void TheChapters_NeedNotBeInOrder()
    {
        PlaybackChapter[] shuffled = [Chapters[2], Chapters[0], Chapters[1]];

        Assert.Equal(1000, ChapterJump.Target(shuffled, 400, forward: true));
        Assert.Equal(300, ChapterJump.Target(shuffled, 400, forward: false));
    }

    [Fact]
    public void WithoutChapters_ThereIsNowhereToGo()
    {
        Assert.Null(ChapterJump.Target([], 100, forward: true));
        Assert.Null(ChapterJump.Target([], 100, forward: false));
    }
}
