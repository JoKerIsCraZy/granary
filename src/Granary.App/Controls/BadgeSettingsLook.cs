using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Granary.App.Controls;

/// <summary>
/// The Navigation &amp; Cards settings' tiles, corners and choices (badges, posters), for x:Bind in their templates: the chosen
/// one in the server's accent.
/// </summary>
public static class BadgeSettingsLook
{
    // A choice (SiloSettingsChoiceButtonStyle): the web's default button when chosen, its outline button when not.
    public static Brush ChoiceFill(bool isSelected) => isSelected ? Accent() : new SolidColorBrush(Colors.Transparent);

    public static Brush ChoiceRing(bool isSelected) => isSelected ? Accent() : new SolidColorBrush(Color.FromArgb(0xFF, 0x28, 0x28, 0x2E));

    public static Brush ChoiceText(bool isSelected) =>
        (Brush)Application.Current.Resources[isSelected ? "SiloOnPrimaryBrush" : "SiloForegroundBrush"];

    public static Brush CornerFill(bool isSelected) => isSelected ? Accent() : new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

    public static Brush TileRing(bool isSelected) => isSelected ? Accent() : new SolidColorBrush(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF));

    // The web's bg-primary/8.
    public static Brush TileFill(bool isSelected) => isSelected
        ? (Brush)Application.Current.Resources["SiloPrimaryTint15Brush"]
        : new SolidColorBrush(Colors.Transparent);

    public static Brush DefaultRing(bool isSelected) => isSelected ? Accent() : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

    public static string AdjustName(string label) => "Adjust " + label;

    public static string MoveUpName(string label) => $"Move {label} up";

    public static string MoveDownName(string label) => $"Move {label} down";

    public static string ShowName(string label) => "Show " + label;

    // A category's chevron: down while folded, up while open.
    public static string Chevron(bool isExpanded) => isExpanded ? "\uE70E" : "\uE70D";

    public static string Folded(bool isExpanded) => isExpanded ? "Expanded" : "Collapsed";

    private static Brush Accent() => (Brush)Application.Current.Resources["SiloPrimaryBrush"];
}
