using System.Globalization;
using Granary.Core.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Granary.App.Controls;

/// <summary>
/// The web settings page's subtitle preview: a 16:9 dark frame with two sample lines drawn in <see cref="Appearance"/>,
/// sized against the frame's height as mpv sizes them against the picture's (1080 lines). <see cref="IsCompact"/> is
/// the player panel's: a 112 px strip with the web player's cue sizes.
/// </summary>
public sealed partial class SubtitlePreview : UserControl
{
    public static readonly DependencyProperty AppearanceProperty = DependencyProperty.Register(nameof(Appearance), typeof(SubtitleAppearance),
        typeof(SubtitlePreview), new PropertyMetadata(SubtitleAppearance.Default, (sender, _) => ((SubtitlePreview)sender).Render()));

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(nameof(IsCompact), typeof(bool),
        typeof(SubtitlePreview), new PropertyMetadata(false, (sender, _) => ((SubtitlePreview)sender).ShowFrame()));

    private static readonly string[] Lines = ["Sample subtitle text", "tuned for readability"];

    private static readonly string[] CompactLines = ["The quick brown fox", "jumps over the lazy dog."];

    // The web player's cue sizes in pixels (lib/subtitleAppearance.ts, FONT_SIZE_MAP), which its panel shows as they are.
    private static readonly Dictionary<string, double> CompactSizes = new()
    {
        ["small"] = 20, ["medium"] = 26, ["large"] = 32, ["xlarge"] = 40, ["xxlarge"] = 48,
    };

    private const double CompactHeight = 112;

    private readonly StackPanel _lines = new() { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 4 };
    private readonly Border _frame;

    public SubtitlePreview()
    {
        var frame = new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0xFF, 0x12, 0x12, 0x26), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0xFF, 0x1D, 0x1D, 0x4A), Offset = 1 },
                },
            },
            Child = _lines,
        };
        _frame = frame;
        Content = frame;
        IsTabStop = false;
        AutomationPropertiesHelper();
        SizeChanged += (_, e) =>
        {
            // 16:9, as wide as the column.
            Height = IsCompact ? CompactHeight : e.NewSize.Width * 9 / 16;
            Render();
        };
    }

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    // The panel's frame: 8 px corners, the web's slate gradient (135°: #1f2937, #0b0f1a at 60 %, #1e293b).
    private void ShowFrame()
    {
        if (IsCompact)
        {
            Height = CompactHeight;
            _frame.CornerRadius = new CornerRadius(8);
            _frame.BorderBrush = new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));
            _frame.Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0xFF, 0x1F, 0x29, 0x37), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0xFF, 0x0B, 0x0F, 0x1A), Offset = 0.6 },
                    new GradientStop { Color = Color.FromArgb(0xFF, 0x1E, 0x29, 0x3B), Offset = 1 },
                },
            };
        }
        Render();
    }

    public SubtitleAppearance Appearance
    {
        get => (SubtitleAppearance)GetValue(AppearanceProperty);
        set => SetValue(AppearanceProperty, value);
    }

    private void AutomationPropertiesHelper() =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, "Subtitle preview: " + string.Join(" ", Lines));

    private void Render()
    {
        var look = Appearance ?? SubtitleAppearance.Default;
        double height = IsCompact ? CompactHeight : ActualHeight > 0 ? ActualHeight : 360;
        double size = IsCompact ? CompactSizes[SubtitleAppearance.SizeName(look.FontSize)] : Math.Max(10, look.FontSize * height / 1080);
        var text = Brush(look.FontColor, look.TextOpacity);
        var edge = Brush(look.TextOutlineColor, 100);
        bool box = look.Background == SubtitleBackground.Box;
        _lines.Children.Clear();
        foreach (string line in IsCompact ? CompactLines : Lines)
        {
            var grid = new Grid();
            // A shadow below and to the right; an outline all round, as libass draws them.
            (double X, double Y)[] offsets = look.Background == SubtitleBackground.Shadow ? [(2.0, 2.0)]
                : look.Background == SubtitleBackground.Outline || look.TextOutline ? new[] { (-1.5, 0.0), (1.5, 0.0), (0.0, -1.5), (0.0, 1.5) }
                : [];
            foreach (var (x, y) in offsets)
            {
                grid.Children.Add(Text(line, look.FontFamily, size, edge, x, y));
            }
            grid.Children.Add(Text(line, look.FontFamily, size, text, 0, 0));
            _lines.Children.Add(new Border
            {
                Child = grid,
                Padding = IsCompact ? new Thickness(12, 4, 12, 4) : box ? new Thickness(size * 0.45, size * 0.1, size * 0.45, size * 0.1) : default,
                CornerRadius = IsCompact ? new CornerRadius(4) : default,
                Background = box ? Brush(look.BackgroundColor, look.BackgroundOpacity) : null,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        _lines.VerticalAlignment = look.Position == SubtitlePosition.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        _lines.Margin = look.Position switch
        {
            SubtitlePosition.Top => new Thickness(0, height * 0.08, 0, 0),
            SubtitlePosition.LowerThird => new Thickness(0, 0, 0, height * (IsCompact ? 0.12 : 0.25)),
            _ => new Thickness(0, 0, 0, height * (IsCompact ? 0.07 : 0.08)),
        };
    }

    private static TextBlock Text(string text, string family, double size, Brush brush, double x, double y) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = new FontFamily(family switch
        {
            SubtitleAppearance.Serif => "Georgia",
            SubtitleAppearance.Monospace => "Consolas",
            SubtitleAppearance.SansSerif => "Segoe UI",
            _ => family,
        }),
        Foreground = brush,
        HorizontalAlignment = HorizontalAlignment.Center,
        RenderTransform = x == 0 && y == 0 ? null : new TranslateTransform { X = x, Y = y },
    };

    private static SolidColorBrush Brush(string hex, int opacityPercent)
    {
        uint rgb = uint.Parse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte alpha = (byte)Math.Round(Math.Clamp(opacityPercent, 0, 100) * 2.55);
        return new SolidColorBrush(Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    }
}
