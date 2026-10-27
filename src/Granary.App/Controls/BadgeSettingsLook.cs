using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Granary.App.Controls;

/// <summary>The badge settings' tiles and corners, for x:Bind in their templates: the chosen one in the server's accent.</summary>
public static class BadgeSettingsLook
{
    public static Brush CornerFill(bool isSelected) => isSelected ? Accent() : new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

    public static Brush TileRing(bool isSelected) => isSelected ? Accent() : new SolidColorBrush(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF));

    // The web's bg-primary/8.
    public static Brush TileFill(bool isSelected) => isSelected
        ? (Brush)Application.Current.Resources["SiloPrimaryTint15Brush"]
        : new SolidColorBrush(Colors.Transparent);

    public static Brush DefaultRing(bool isSelected) => isSelected ? Accent() : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

    public static string AdjustName(string label) => "Adjust " + label;

    // A category's chevron: down while folded, up while open.
    public static string Chevron(bool isExpanded) => isExpanded ? "\uE70E" : "\uE70D";

    public static string Folded(bool isExpanded) => isExpanded ? "Expanded" : "Collapsed";

    private static Brush Accent() => (Brush)Application.Current.Resources["SiloPrimaryBrush"];
}
