using Microsoft.UI.Xaml;

namespace Granary.App.Controls;

/// <summary>
/// Words the settings' search finds a setting or page by besides its texts, kept next to it in the markup:
/// <c>controls:SearchKeywords.Terms="lip sync, sound delay"</c> on a SettingsRow, a SettingsGroup or a sidebar entry. They are
/// never shown, and as the web's keywords they are English and match in every language. Search reads them from the
/// markup (SettingsMarkup); the property only lets the markup carry them.
/// </summary>
public static class SearchKeywords
{
    public static readonly DependencyProperty TermsProperty =
        DependencyProperty.RegisterAttached("Terms", typeof(string), typeof(SearchKeywords), new PropertyMetadata(""));

    public static string GetTerms(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string)element.GetValue(TermsProperty);
    }

    public static void SetTerms(DependencyObject element, string value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TermsProperty, value);
    }
}
