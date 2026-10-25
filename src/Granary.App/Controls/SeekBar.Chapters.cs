using Granary.Core.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Granary.App.Controls;

// Chapters as Silo's web player marks them: a thin tick where each starts, and the chapter's title under the time in
// the bubble.
public sealed partial class SeekBar
{
    public static readonly DependencyProperty ChaptersProperty = DependencyProperty.Register(nameof(Chapters),
        typeof(IReadOnlyList<PlaybackChapter>), typeof(SeekBar), new PropertyMetadata(null, (sender, _) => ((SeekBar)sender).OnChaptersChanged()));

    private const double ChapterTickHeight = 10;

    /// <summary>The chapters of the title, in seconds of <see cref="Duration"/>.</summary>
    public IReadOnlyList<PlaybackChapter>? Chapters
    {
        get => (IReadOnlyList<PlaybackChapter>?)GetValue(ChaptersProperty);
        set => SetValue(ChaptersProperty, value);
    }

    private void OnChaptersChanged()
    {
        ChapterTicks.Children.Clear();
        var tick = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
        foreach (var chapter in Chapters ?? [])
        {
            ChapterTicks.Children.Add(new Rectangle
            {
                Width = 1,
                Height = ChapterTickHeight,
                HorizontalAlignment = HorizontalAlignment.Left,
                Fill = tick,
                Tag = chapter,
            });
        }
        Render();
    }

    // None at the very start or at the end: a chapter that starts there marks nothing.
    private void PlaceChapterTicks(double width)
    {
        foreach (var tick in ChapterTicks.Children.OfType<Rectangle>())
        {
            double start = ((PlaybackChapter)tick.Tag).StartSeconds;
            bool shows = Duration > 0 && width > 0 && start > 0 && start < Duration;
            tick.Visibility = shows ? Visibility.Visible : Visibility.Collapsed;
            if (shows)
            {
                tick.Margin = new Thickness(start / Duration * width - 0.5, 0, 0, 0);
            }
        }
    }

    // The chapter that holds the time, else the last, as the web player's bubble names it.
    private void ShowTipChapter(double seconds)
    {
        var chapters = Chapters ?? [];
        var chapter = chapters.FirstOrDefault(c => c.Contains(seconds)) ?? chapters.LastOrDefault();
        TipChapter.Text = chapter?.Title ?? "";
        TipChapter.Visibility = string.IsNullOrWhiteSpace(chapter?.Title) ? Visibility.Collapsed : Visibility.Visible;
    }
}
