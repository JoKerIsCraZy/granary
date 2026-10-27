using Granary.Core.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Granary.App.Themes;

/// <summary>
/// Paints the server's accent (<see cref="ServerAccent"/>) into the app's resources, as the web lays its accent over Cinema
/// Dark: the accent brushes of Themes/Silo.xaml and the Fluent accent brushes of its theme dictionaries take the new
/// colour and keep their own alpha, so every element that refers to one changes at once. The page dictionaries
/// (SiloSettings, SiloAuth) refer to the same brushes. Runs on the UI thread.
/// </summary>
internal static class AccentPainter
{
    private static readonly string[] PrimaryKeys =
    [
        "SiloPrimaryBrush", "SiloPrimaryTint15Brush", "SiloPrimaryTint60Brush", "SiloPrimaryTint75Brush",
        "AccentFillColorDefaultBrush", "AccentTextFillColorTertiaryBrush",
    ];

    private static readonly string[] HoverKeys = ["SiloPrimaryHoverBrush", "AccentFillColorSecondaryBrush"];

    private static readonly string[] PressedKeys = ["SiloPrimaryPressedBrush", "AccentFillColorTertiaryBrush"];

    // Fluent's accent text reads on the dark page: a lighter shade.
    private static readonly string[] LightTextKeys = ["AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush"];

    private static readonly string[] LabelKeys = ["SiloOnPrimaryBrush"];

    private static readonly string[] BadgeLabelKeys = ["SiloOnPrimaryBadgeBrush", "TextOnAccentFillColorPrimaryBrush"];

    // Granary's own lighter red for accent text (#F87171), kept for its own accent.
    private static readonly RgbColor DefaultLightText = new(0xf8, 0x71, 0x71);

    public static void Apply(AccentPalette palette)
    {
        var dictionaries = AllDictionaries(Application.Current.Resources).ToList();
        Paint(dictionaries, PrimaryKeys, palette.Primary);
        Paint(dictionaries, HoverKeys, palette.Hover);
        Paint(dictionaries, PressedKeys, palette.Pressed);
        Paint(dictionaries, LightTextKeys, palette.IsDefault ? DefaultLightText : palette.Primary.Mix(AccentPalette.White, 0.25));
        Paint(dictionaries, LabelKeys, palette.Label);
        Paint(dictionaries, BadgeLabelKeys, palette.BadgeLabel);
        foreach (var dictionary in dictionaries)
        {
            if (dictionary.TryGetValue("SiloAccentGlowBrush", out var glow) && glow is GradientBrush gradient)
            {
                foreach (var stop in gradient.GradientStops)
                {
                    stop.Color = WithAlpha(palette.Primary, stop.Color.A);
                }
            }
        }
    }

    private static void Paint(List<ResourceDictionary> dictionaries, string[] keys, RgbColor color)
    {
        foreach (var dictionary in dictionaries)
        {
            foreach (string key in keys)
            {
                if (dictionary.TryGetValue(key, out var value) && value is SolidColorBrush brush)
                {
                    brush.Color = WithAlpha(color, brush.Color.A);
                }
            }
        }
    }

    private static Color WithAlpha(RgbColor color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    // The app's dictionaries, the ones they merge, and their theme dictionaries.
    private static IEnumerable<ResourceDictionary> AllDictionaries(ResourceDictionary dictionary)
    {
        yield return dictionary;
        foreach (var theme in dictionary.ThemeDictionaries.Values.OfType<ResourceDictionary>())
        {
            foreach (var inner in AllDictionaries(theme))
            {
                yield return inner;
            }
        }
        foreach (var merged in dictionary.MergedDictionaries)
        {
            foreach (var inner in AllDictionaries(merged))
            {
                yield return inner;
            }
        }
    }
}
