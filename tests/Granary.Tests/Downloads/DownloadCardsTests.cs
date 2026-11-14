using Granary.Core.Downloads;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Downloads;
using Granary.Tests.Fakes;

namespace Granary.Tests.Downloads;

/// <summary>
/// A download's card while it comes: its status over the two lines under its title, each short enough for the narrowest
/// poster, the whole line in its tip, and a name for screen readers that does not change with every step of its bar.
/// </summary>
public sealed class DownloadCardsTests
{
    private const long KB = 1024;
    private const long MB = 1024 * KB;
    private const long GB = 1024 * MB;

    // The font the card's lines are drawn in (SiloCaptionTextStyle: Plus Jakarta Sans Medium, 12 px).
    private static readonly FontWidths Caption = FontWidths.Load("src/Granary.App/Assets/Fonts/PlusJakartaSans-Medium.ttf");

    private static DownloadItem Coming(long expected, long done, DownloadState state = DownloadState.Downloading, string? seriesId = null) =>
        new("dl_1", "mv_1", "Example Movie", seriesId, seriesId is null ? null : "Example Series", seriesId is null ? null : 1,
            seriesId is null ? null : 5, state, DownloadFailure.None, IsPausedByViewer: false, IsNoLongerAvailable: false, FilesMissing: false,
            WaitsForUnmetered: false, ExpectedBytes: expected, BytesDone: done, PreparationProgress: null, PosterPath: null, MediaFileId: null);

    // What a line has to fit: the narrowest compact poster in a 1920 px window, its lines 2 px in from each side. The grid
    // is the window less the sidebar (260 px) and the page's padding (48 px each side), in the web's columns.
    private static double Room()
    {
        var compact = new CardPresentation(PosterSize.Compact, CardCaption.TitleMetadata);
        double grid = 1920 - 260 - 2 * 48;
        int columns = compact.GridColumns(WindowBreakpoint.ExtraLarge, grid);
        return (grid - (columns - 1) * compact.GridGap) / columns - 2 * 2;
    }

    [Fact]
    public void ADownloadingCard_SaysItsStatusOverItsTwoLines_AndTheWholeLineInItsTip()
    {
        // 42 % of 8 GB, the rest at 12.3 MB a second: a little over 6 minutes.
        var item = Coming(8 * GB, (8 * GB * 42 / 100) + 1);

        var measuring = DownloadCards.Download(item);
        var measured = DownloadCards.Download(item, bytesPerSecond: 12.3 * MB);

        // Until the speed is known: where it stands, then its size.
        Assert.Equal(("Downloading · 42%", "8.0 GB", "Downloading · 42%"), (measuring.Subtitle, measuring.Caption, measuring.ToolTip));
        // Then how far and how long, then how fast; the tip has it all in one line, in the apps' order.
        Assert.Equal(("42% · 6 min left", "12.3 MB/s", "Downloading · 42% · 12.3 MB/s · 6 min left"),
            (measured.Subtitle, measured.Caption, measured.ToolTip));
        Assert.True(measured.PlainSubtitle);
    }

    [Fact]
    public void ADownloadingCardsLines_EachFitTheNarrowestCompactPoster()
    {
        double room = Room();
        double[] speeds = [1, 999.9 * KB, 12.3 * MB, 999.9 * MB];
        TimeSpan[] left =
        [
            TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(65),
            new TimeSpan(9, 59, 0), new TimeSpan(99, 59, 0),
        ];
        var lines = new List<string>();
        foreach (int percent in new[] { 0, 42, 99 })
        {
            foreach (double speed in speeds)
            {
                foreach (var time in left)
                {
                    long rest = (long)(time.TotalSeconds * speed);
                    long expected = rest * 100 / (100 - percent);
                    var card = DownloadCards.Download(Coming(expected, expected - rest), speed);
                    lines.Add(card.Subtitle!);
                    lines.Add(card.Caption!);
                }
            }
            foreach (long size in new[] { 700 * MB, 8 * GB, 999 * GB })
            {
                var card = DownloadCards.Download(Coming(size, size * percent / 100));
                lines.Add(card.Subtitle!);
                lines.Add(card.Caption!);
            }
        }

        var tooWide = lines.Distinct().Where(line => Caption.Width(line, 12) > room).ToList();

        Assert.True(room > 140, $"A compact poster is {room:0.0} px");
        Assert.Empty(tooWide);
    }

    [Fact]
    public void ADownloadingCardsName_StaysTheSameAsItComes()
    {
        var names = new[]
        {
            DownloadCards.Download(Coming(8 * GB, GB)),
            DownloadCards.Download(Coming(8 * GB, 2 * GB), 12.3 * MB),
            DownloadCards.Download(Coming(8 * GB, 5 * GB), 3.1 * MB),
        }.Select(card => card.AccessibleName).Distinct();

        // What changes is the bar, where screen readers read a value.
        Assert.Equal(["Example Movie, 8.0 GB, Downloading"], names);
    }

    [Fact]
    public void AnEpisodesCard_SaysItsStatusTheSameWay()
    {
        var card = DownloadCards.Download(Coming(2 * GB, GB, seriesId: "sr_1"), 2 * MB);

        Assert.Equal(("S01E05 · Example Movie", "50% · 9 min left", "2.0 MB/s"), (card.Title, card.Subtitle, card.Caption));
    }

    [Fact]
    public void ACardThatDoesNotCome_KeepsItsSize_ThenItsStatus_WithNoTipOnceWhole()
    {
        var queued = DownloadCards.Download(Coming(8 * GB, GB, DownloadState.Queued), 12.3 * MB);
        var whole = DownloadCards.Download(Coming(8 * GB, 8 * GB, DownloadState.Completed));

        Assert.Equal(("8.0 GB", "Queued", "Queued"), (queued.Subtitle, queued.Caption, queued.ToolTip));
        Assert.Equal(("8.0 GB", "Downloaded", (string?)null), (whole.Subtitle, whole.Caption, whole.ToolTip));
    }

    [Fact]
    public void ASeriesCard_HasNoBar_BeforeAnyOfItsBytesCame()
    {
        var card = DownloadCards.Series("sr_1", [Coming(2 * GB, 0, DownloadState.Queued, "sr_1")]);

        Assert.Null(card.Progress);
    }
}
