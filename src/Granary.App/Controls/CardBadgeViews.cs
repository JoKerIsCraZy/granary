using Granary.Core.Overlays;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Granary.App.Controls;

/// <summary>
/// Draws a card badge in the profile's style (the web's presets, at its 185 px reference poster): the fill, text and
/// border the style gives the badge's colour, its icon, and its word. Icons that are the word itself ("ATMOS", "HDR10")
/// draw as a heavy word, as Silo writes them.
/// </summary>
public static class CardBadgeViews
{
    // Every preset's space between a badge's icon and its label.
    private const double IconGap = 4;

    // The web's icon ids as Segoe Fluent glyphs; brand marks without a glyph draw as their word, or not at all.
    private static readonly Dictionary<string, string> Glyphs = new(StringComparer.Ordinal)
    {
        ["monitor"] = "\uE7F4",
        ["tv"] = "\uE7F4",
        ["volume"] = "\uE767",
        ["film"] = "\uE8B2",
        ["layout"] = "\uE8A9",
        ["languages"] = "\uF2B7",
        ["subtitles"] = "\uE7F0",
        ["shield"] = "\uEA18",
        ["users"] = "\uE716",
        ["clock"] = "\uE823",
        ["globe"] = "\uE774",
        ["building"] = "\uE80F",
        ["calendar"] = "\uE787",
        ["download"] = "\uE896",
        ["hourglass"] = "\uE916",
        ["alert"] = "\uE7BA",
        ["star"] = "\uE734",
        ["award"] = "\uE734",
        ["ribbon"] = "\uE734",
    };

    public static FrameworkElement Create(CardBadge badge, OverlayPreset preset, bool alignRight)
    {
        var (background, foreground, border, leftOnly) = preset.Colors(badge.Accent);
        var text = Brush(foreground);
        // A grid, not a row: the label takes the width left beside the icon and ends in an ellipsis when its corner is short.
        var content = new Grid { ColumnSpacing = IconGap, VerticalAlignment = VerticalAlignment.Center };
        if (badge.IconId is { } icon)
        {
            if (Glyphs.TryGetValue(icon, out string? glyph))
            {
                Add(new FontIcon { Glyph = glyph, FontSize = preset.IconSize, Foreground = text, VerticalAlignment = VerticalAlignment.Center }, GridLength.Auto);
            }
            else if (CardBadges.WordmarkOf(icon) is { } word)
            {
                Add(Word(word, preset, text, "SiloFontExtraBold"), GridLength.Auto);
            }
        }
        if (badge.Text.Length > 0)
        {
            Add(Word(badge.Text, preset, text, preset.IsBold ? "SiloFontBold" : "SiloFontSemiBold"), new GridLength(1, GridUnitType.Star));
        }
        var view = new Border
        {
            Child = content,
            Background = Brush(background),
            BorderBrush = Brush(border),
            BorderThickness = leftOnly ? new Thickness(2, 0, 0, 0) : new Thickness(border.A == 0 ? 0 : preset.BorderWidth),
            Padding = new Thickness(preset.PaddingX, preset.PaddingY, preset.PaddingX, preset.PaddingY),
            HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(preset.CornerRadius ?? 999),
            IsHitTestVisible = false,
        };
        if (preset.CornerRadius is null)
        {
            Pill.SetIsEnabled(view, true);
        }
        return view;

        void Add(FrameworkElement part, GridLength width)
        {
            Grid.SetColumn(part, content.ColumnDefinitions.Count);
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            content.Children.Add(part);
        }
    }

    /// <summary>The style picker's "4K" in a style; Vibrant's in IMDb gold, as on the web.</summary>
    public static FrameworkElement Sample(OverlayPresetId preset) =>
        Create(new CardBadge("sample", "4K", OverlayPosition.TopLeft, null, preset == OverlayPresetId.Vibrant ? "#f5c518" : null),
            OverlayPreset.Of(preset), alignRight: false);

    private static TextBlock Word(string text, OverlayPreset preset, Brush foreground, string font)
    {
        var word = new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.Resources["SiloBadgeTextStyle"],
            FontSize = preset.FontSize,
            CharacterSpacing = preset.CharacterSpacing,
            FontFamily = (FontFamily)Application.Current.Resources[font],
            FontWeight = font == "SiloFontSemiBold" ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Bold,
            Foreground = foreground,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return word;
    }

    private static SolidColorBrush Brush(BadgeColor color) => new(Color.FromArgb(color.A, color.R, color.G, color.B));
}
