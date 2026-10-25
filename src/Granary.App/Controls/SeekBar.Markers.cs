using Granary.Core.Api.Playback;
using Granary.Core.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Granary.App.Controls;

// The marked parts on the track, tinted as Silo's web player tints them (sky intro, violet recap, amber credits,
// emerald preview), under the played part; the bubble names the part under the pointer.
public sealed partial class SeekBar
{
    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(nameof(Markers),
        typeof(IReadOnlyList<PlaybackMarker>), typeof(SeekBar), new PropertyMetadata(null, (sender, _) => ((SeekBar)sender).OnMarkersChanged()));

    /// <summary>The marked parts of the title, in seconds of <see cref="Duration"/>.</summary>
    public IReadOnlyList<PlaybackMarker>? Markers
    {
        get => (IReadOnlyList<PlaybackMarker>?)GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    private static (Color Tint, Color Dot, string Name) Look(string kind) => kind switch
    {
        WatchMarkerKinds.Intro => (Color.FromArgb(0x66, 0x7D, 0xD3, 0xFC), Color.FromArgb(0xFF, 0x7D, 0xD3, 0xFC), "Intro"),
        WatchMarkerKinds.Recap => (Color.FromArgb(0x66, 0xC4, 0xB5, 0xFD), Color.FromArgb(0xFF, 0xC4, 0xB5, 0xFD), "Recap"),
        WatchMarkerKinds.Credits => (Color.FromArgb(0x73, 0xFC, 0xD3, 0x4D), Color.FromArgb(0xFF, 0xFC, 0xD3, 0x4D), "Credits / Outro"),
        _ => (Color.FromArgb(0x66, 0x6E, 0xE7, 0xB7), Color.FromArgb(0xFF, 0x6E, 0xE7, 0xB7), "Preview"),
    };

    private void OnMarkersChanged()
    {
        Regions.Children.Clear();
        foreach (var marker in Markers ?? [])
        {
            Regions.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(1.5),
                Background = new SolidColorBrush(Look(marker.Kind).Tint),
                Tag = marker,
            });
        }
        Render();
    }

    private void PlaceRegions(double width)
    {
        foreach (var region in Regions.Children.OfType<Border>())
        {
            var marker = (PlaybackMarker)region.Tag;
            bool shows = Duration > 0 && width > 0 && marker.StartSeconds < Duration;
            region.Visibility = shows ? Visibility.Visible : Visibility.Collapsed;
            if (shows)
            {
                double start = marker.StartSeconds / Duration * width;
                double end = Math.Min(marker.EndSeconds, Duration) / Duration * width;
                region.Margin = new Thickness(start, 0, 0, 0);
                region.Width = Math.Max(0, end - start);
            }
        }
    }

    // The shortest part that holds the time, as the web player picks among parts that overlap.
    private void ShowTipRegion(double seconds)
    {
        var marker = (Markers ?? []).Where(m => m.Contains(seconds)).MinBy(m => m.EndSeconds - m.StartSeconds);
        TipRegion.Visibility = marker is null ? Visibility.Collapsed : Visibility.Visible;
        if (marker is null)
        {
            return;
        }
        var look = Look(marker.Kind);
        TipRegionDot.Fill = new SolidColorBrush(look.Dot);
        TipRegionName.Text = look.Name;
        TipRegionRange.Text = PlayerTime.Format(marker.StartSeconds, Duration) + "-" + PlayerTime.Format(marker.EndSeconds, Duration);
    }
}
