using Granary.Core.ViewModels.Admin;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Granary.App.Controls;

/// <summary>
/// The dashboard's line chart, as the web's LineChart: a rate over a window on gridlines at human numbers, the line in
/// --chart-1 over a faint fill; where the sampler wrote nothing the line breaks. "24h ago" and "now" below.
/// </summary>
internal sealed partial class RateChart : Grid
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(AdminRateSeries), typeof(RateChart), new PropertyMetadata(null, (d, _) => ((RateChart)d).Render()));

    public static readonly DependencyProperty StartLabelProperty = DependencyProperty.Register(
        nameof(StartLabel), typeof(string), typeof(RateChart), new PropertyMetadata("", (d, _) => ((RateChart)d).Render()));

    private const double AxisWidth = 36;
    private const double LabelHeight = 18;
    private const double TopInset = 6;

    private readonly Canvas _plot = new();

    public RateChart()
    {
        Children.Add(_plot);
        SizeChanged += (_, _) => Render();
    }

    public AdminRateSeries? Series
    {
        get => (AdminRateSeries?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    /// <summary>Where the window starts, "24h ago"; it ends "now".</summary>
    public string StartLabel
    {
        get => (string)GetValue(StartLabelProperty);
        set => SetValue(StartLabelProperty, value);
    }

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    private void Render()
    {
        _plot.Children.Clear();
        var values = Series?.Values ?? [];
        double width = ActualWidth - AxisWidth;
        double height = ActualHeight - LabelHeight - TopInset;
        if (values.Count == 0 || width <= 0 || height <= 0)
        {
            return;
        }
        var ticks = AdminCharts.NiceTicks(Series!.Peak);
        double top = ticks[^1] > 0 ? ticks[^1] : 1;
        foreach (double tick in ticks)
        {
            double y = TopInset + (1 - tick / top) * height;
            var line = new Line { X1 = AxisWidth, X2 = AxisWidth + width, Y1 = y, Y2 = y, StrokeThickness = 1, Stroke = Resource("SiloBorderBrush"), Opacity = tick == 0 ? 0.9 : 0.5 };
            _plot.Children.Add(line);
            AddText(AdminText.MbpsValue(tick), 0, y - 7, AxisWidth - 6, TextAlignment.Right);
        }
        var color = PlaybackChart.SeriesColors[0];
        double step = values.Count > 1 ? width / (values.Count - 1) : 0;
        foreach (var run in Runs(values))
        {
            var points = new PointCollection();
            foreach (int i in run)
            {
                points.Add(new Point(AxisWidth + i * step, TopInset + (1 - values[i]!.Value / top) * height));
            }
            var area = new PointCollection();
            foreach (var point in points)
            {
                area.Add(point);
            }
            area.Add(new Point(points[^1].X, TopInset + height));
            area.Add(new Point(points[0].X, TopInset + height));
            _plot.Children.Add(new Polygon { Points = area, Fill = new SolidColorBrush(color) { Opacity = 0.12 } });
            _plot.Children.Add(new Polyline { Points = points, Stroke = new SolidColorBrush(color), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round });
        }
        AddText(StartLabel, AxisWidth, TopInset + height + 4, width / 2, TextAlignment.Left);
        AddText("now", AxisWidth + width / 2, TopInset + height + 4, width / 2, TextAlignment.Right);
    }

    // The indexes of each stretch of samples between gaps.
    private static IEnumerable<List<int>> Runs(IReadOnlyList<double?> values)
    {
        var run = new List<int>();
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] is null)
            {
                if (run.Count > 0)
                {
                    yield return run;
                    run = [];
                }
                continue;
            }
            run.Add(i);
        }
        if (run.Count > 0)
        {
            yield return run;
        }
    }

    private void AddText(string text, double left, double top, double width, TextAlignment alignment)
    {
        var block = new TextBlock { Text = text, FontSize = 10, Foreground = Resource("SiloMutedBrush"), Width = width, TextAlignment = alignment };
        Canvas.SetLeft(block, left);
        Canvas.SetTop(block, top);
        _plot.Children.Add(block);
    }
}
