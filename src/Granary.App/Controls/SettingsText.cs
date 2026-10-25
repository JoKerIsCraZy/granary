using Microsoft.UI.Xaml;

namespace Granary.App.Controls;

/// <summary>Small functions for x:Bind on the settings pages, which call them on a type.</summary>
public static class SettingsText
{
    public static bool Has(string text) => !string.IsNullOrEmpty(text);

    public static bool Not(bool value) => !value;

    public static Visibility Shows(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    // Settings a style does not use are dimmed, as on the web.
    public static double Dim(bool isUsed) => isUsed ? 1 : 0.4;
}
