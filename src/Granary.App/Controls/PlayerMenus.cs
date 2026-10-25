using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Controls;

/// <summary>
/// For the rows of the player's menus. Static, because x:Bind in a data template calls functions on a type, not on
/// the page.
/// </summary>
public static class PlayerMenus
{
    public static Visibility Shows(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    // Chips are upper case on screen, as on the web; the row's accessible name keeps the words as written. Codec
    // names are identifiers, not prose: "vorbis" stays VORBIS in every culture.
    public static string Upper(string text) => text.ToUpperInvariant();

    // The chosen row's name is blue, the others white.
    public static Brush RowForeground(bool isSelected) =>
        (Brush)Application.Current.Resources[isSelected ? "SiloPlayerMenuChosenBrush" : "SiloPlayerMenuRowBrush"];

    public static string Status(bool isSelected) => isSelected ? "Selected" : "";
}
