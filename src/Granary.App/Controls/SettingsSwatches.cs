using System.Globalization;
using Granary.Core.Texts;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Granary.App.Controls;

/// <summary>The colour swatches of the subtitle look, for x:Bind in their template.</summary>
public static class SettingsSwatches
{
    public static Brush Fill(string hex)
    {
        uint rgb = uint.Parse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new SolidColorBrush(Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    }

    // The chosen colour has a white ring, the others none.
    public static Brush Ring(bool isSelected) => new SolidColorBrush(isSelected ? Colors.White : Colors.Transparent);

    // What a screen reader adds to the chosen one.
    public static string Status(bool isSelected) => isSelected ? AppText.Get("granary.settings.selected") : "";
}
