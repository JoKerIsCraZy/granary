using Granary.Core.Playback;
using Granary.Core.ViewModels.Player;

namespace Granary.Tests.ViewModels;

/// <summary>The rows of the Chapters menu.</summary>
public sealed class ChapterOptionTests
{
    private static readonly PlaybackChapter[] Chapters =
    [
        new("Opening", 0, 300, new Uri("https://silo.test/1.webp")),
        new("  ", 300, 900, null),
        new("Heist", 1000, 1800, null),
    ];

    [Fact]
    public void TheRows_MarkTheOneThatPlays_AndNameAnUntitledChapterByItsPlace()
    {
        var rows = ChapterOption.For(Chapters, 1);

        Assert.Equal(
            [
                new ChapterOption("Opening", 0, new Uri("https://silo.test/1.webp"), false),
                new ChapterOption("Chapter 2", 300, null, true),
                new ChapterOption("Heist", 1000, null, false),
            ],
            rows);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(299.9, 0)]
    [InlineData(300, 1)]
    [InlineData(950, -1)]
    [InlineData(1800, -1)]
    public void TheChapterThatPlays_HoldsThePosition(double seconds, int expected) =>
        Assert.Equal(expected, ChapterOption.IndexAt(Chapters, seconds));
}
