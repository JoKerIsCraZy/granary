using System.Globalization;
using Granary.Core.ViewModels.Admin;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Granary.App.Controls;

/// <summary>
/// The dashboard's playback chart, as the web's StackedColumnChart: a column a bucket, direct play at its foot, direct
/// stream above, transcode on top, 2 px between the parts; a column's tooltip names its counts. Four time labels below.
/// </summary>
internal sealed partial class PlaybackChart : Grid
{
    /// <summary>The web's --chart-1, --chart-2 and --chart-3: direct play, direct stream, transcode.</summary>
    public static readonly Color[] SeriesColors = [Color.FromArgb(0xFF, 0x5F, 0x74, 0xEE), Color.FromArgb(0xFF, 0x16, 0x9E, 0x88), Color.FromArgb(0xFF, 0xBF, 0x7F, 0x22)];

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(IReadOnlyList<AdminPlaybackColumn>), typeof(PlaybackChart), new PropertyMetadata(null, (d, _) => ((PlaybackChart)d).Render()));

    private const double LabelHeight = 18;
    private const double Gap = 2;
    private const double MaxColumnWidth = 24;
    private const double MinSegment = 2;
    private const int TargetLabels = 4;
    private static readonly string[] SeriesNames = ["Direct play", "Direct stream", "Transcode"];

    private readonly Canvas _plot = new();
    private readonly Canvas _labels = new() { Height = LabelHeight };

    public PlaybackChart()
    {
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SetRow(_labels, 1);
        Children.Add(_plot);
        Children.Add(_labels);
        SizeChanged += (_, _) => Render();
    }

    public IReadOnlyList<AdminPlaybackColumn>? Columns
    {
        get => (IReadOnlyList<AdminPlaybackColumn>?)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    private static Brush Muted => (Brush)Application.Current.Resources["SiloMutedBrush"];

    private void Render()
    {
        _plot.Children.Clear();
        _labels.Children.Clear();
        var columns = Columns ?? [];
        double width = ActualWidth;
        double height = ActualHeight - LabelHeight;
        if (columns.Count == 0 || width <= 0 || height <= 0)
        {
            return;
        }
        double max = Math.Max(1, columns.Max(c => c.Total));
        double slot = (width - Gap * (columns.Count - 1)) / columns.Count;
        bool daily = columns.Count > 1 && columns[1].Start - columns[0].Start >= TimeSpan.FromDays(1);
        int stride = Math.Max(1, (int)Math.Ceiling(columns.Count / (double)TargetLabels));
        for (int i = 0; i < columns.Count; i++)
        {
            double left = i * (slot + Gap);
            AddColumn(columns[i], left, slot, height, max, daily);
            if (i % stride == 0)
            {
                AddLabel(BucketLabel(columns[i].Start, daily), left + slot / 2, i / (double)columns.Count, width);
            }
        }
        var baseline = new Border { Width = width, Height = 1, Background = (Brush)Application.Current.Resources["SiloBorderBrush"] };
        Canvas.SetTop(baseline, height - 1);
        _plot.Children.Add(baseline);
    }

    private void AddColumn(AdminPlaybackColumn column, double left, double slot, double height, double max, bool daily)
    {
        int[] values = [column.Direct, column.DirectStream, column.Transcode];
        double barWidth = Math.Min(MaxColumnWidth, slot);
        double x = left + (slot - barWidth) / 2;
        double bottom = height;
        int top = Array.FindLastIndex(values, v => v > 0);
        for (int s = 0; s < values.Length; s++)
        {
            if (values[s] <= 0)
            {
                continue;
            }
            double segment = Math.Max(MinSegment, values[s] / max * height);
            var bar = new Border
            {
                Width = barWidth,
                Height = segment,
                Background = new SolidColorBrush(SeriesColors[s]),
                CornerRadius = s == top ? new CornerRadius(4, 4, 0, 0) : default,
            };
            Canvas.SetLeft(bar, x);
            Canvas.SetTop(bar, bottom - segment);
            _plot.Children.Add(bar);
            bottom -= segment + Gap;
        }
        // The whole column answers the pointer and the reader, as the web's column does.
        string summary = $"{BucketLabel(column.Start, daily)}: {AdminText.Sessions(column.Total)}";
        var hit = new Border { Width = slot, Height = height, Background = new SolidColorBrush(Colors.Transparent) };
        AutomationProperties.SetName(hit, summary);
        ToolTipService.SetToolTip(hit, string.Join(Environment.NewLine,
            [BucketLabel(column.Start, daily), .. SeriesNames.Select((name, s) => $"{name}  {values[s]}"), $"Sessions  {column.Total}"]));
        Canvas.SetLeft(hit, left);
        _plot.Children.Add(hit);
    }

    // At the edges the label stays inside the chart, as the web's ticks do.
    private void AddLabel(string text, double center, double ratio, double width)
    {
        var label = new TextBlock { Text = text, FontSize = 10, Foreground = Muted };
        label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double labelWidth = label.DesiredSize.Width;
        double left = ratio < 0.1 ? center - Math.Min(center, labelWidth / 2) : center - labelWidth / 2;
        Canvas.SetLeft(label, Math.Clamp(left, 0, Math.Max(0, width - labelWidth)));
        Canvas.SetTop(label, 4);
        _labels.Children.Add(label);
    }

    // The hour in the viewer's own time ("3 PM"), or the day for daily buckets ("Oct 27").
    private static string BucketLabel(DateTimeOffset start, bool daily) =>
        start.ToLocalTime().ToString(daily ? "MMM d" : "h tt", CultureInfo.GetCultureInfo("en-US"));
}
