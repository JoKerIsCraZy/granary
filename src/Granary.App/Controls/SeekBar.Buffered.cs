using Granary.Core.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Granary.App.Controls;

// What the player holds of the stream as Silo's web player draws a video's buffered ranges: white 30 % stretches of the
// track, under the marked parts and the played part.
public sealed partial class SeekBar
{
    public static readonly DependencyProperty BufferedProperty = DependencyProperty.Register(nameof(Buffered),
        typeof(BufferedRanges), typeof(SeekBar), new PropertyMetadata(BufferedRanges.None, (sender, _) => ((SeekBar)sender).OnBufferedChanged()));

    private readonly SolidColorBrush _bufferedBrush = new(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));

    // What the parts were last placed for. Render runs with every position tick, which leaves the ranges where they are.
    private (BufferedRanges? Ranges, double Duration, double Width) _bufferedPlaced;

    /// <summary>The stretches the player holds, in seconds of <see cref="Duration"/>; they change a few times a second while it reads.</summary>
    public BufferedRanges? Buffered
    {
        get => (BufferedRanges?)GetValue(BufferedProperty);
        set => SetValue(BufferedProperty, value);
    }

    // The parts stay and move with the ranges; one is added only for more ranges than ever before, and those over hide.
    private void OnBufferedChanged()
    {
        int count = Buffered?.Count ?? 0;
        while (BufferedParts.Children.Count < count)
        {
            BufferedParts.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(1.5),
                Background = _bufferedBrush,
            });
        }
        PlaceBuffered(Track.ActualWidth);
    }

    private void PlaceBuffered(double width)
    {
        var (placedRanges, placedDuration, placedWidth) = _bufferedPlaced;
        if (ReferenceEquals(Buffered, placedRanges) && Duration == placedDuration && width == placedWidth)
        {
            return;
        }
        _bufferedPlaced = (Buffered, Duration, width);
        var ranges = Buffered ?? BufferedRanges.None;
        var parts = BufferedParts.Children;
        for (int i = 0; i < parts.Count; i++)
        {
            var part = (Border)parts[i];
            bool shows = i < ranges.Count && Duration > 0 && width > 0;
            part.Visibility = shows ? Visibility.Visible : Visibility.Collapsed;
            if (shows)
            {
                double start = Math.Clamp(ranges[i].StartSeconds / Duration, 0, 1) * width;
                double end = Math.Clamp(ranges[i].EndSeconds / Duration, 0, 1) * width;
                part.Margin = new Thickness(start, 0, 0, 0);
                part.Width = Math.Max(0, end - start);
            }
        }
    }
}
