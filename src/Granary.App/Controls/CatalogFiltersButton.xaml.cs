using System.Globalization;
using Granary.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Globalization.NumberFormatting;

namespace Granary.App.Controls;

/// <summary>The Filters pill and its flyout, for a <see cref="LibraryFiltersViewModel"/>.</summary>
public sealed partial class CatalogFiltersButton : UserControl
{
    public static readonly DependencyProperty FiltersProperty =
        DependencyProperty.Register(nameof(Filters), typeof(LibraryFiltersViewModel), typeof(CatalogFiltersButton),
            new PropertyMetadata(null, (d, _) => ((CatalogFiltersButton)d).Bindings.Update()));

    public static readonly DependencyProperty OptionsErrorTextProperty =
        DependencyProperty.Register(nameof(OptionsErrorText), typeof(string), typeof(CatalogFiltersButton),
            new PropertyMetadata("Couldn't load the genres and ratings. The year range still works."));

    public CatalogFiltersButton()
    {
        InitializeComponent();
        // Years without grouping or decimals: "1999", not "1,999.00".
        var years = new DecimalFormatter { IntegerDigits = 1, FractionDigits = 0, IsGrouped = false };
        YearFromBox.NumberFormatter = years;
        YearToBox.NumberFormatter = years;
    }

    /// <summary>The filters the flyout edits.</summary>
    public LibraryFiltersViewModel? Filters
    {
        get => (LibraryFiltersViewModel?)GetValue(FiltersProperty);
        set => SetValue(FiltersProperty, value);
    }

    /// <summary>What the flyout says when the genres and ratings could not be read.</summary>
    public string OptionsErrorText
    {
        get => (string)GetValue(OptionsErrorTextProperty);
        set => SetValue(OptionsErrorTextProperty, value);
    }

    public string CountText(int count) => count.ToString(CultureInfo.InvariantCulture);

    public string FiltersName(int activeCount) => activeCount switch
    {
        0 => "Filters",
        1 => "Filters, 1 active",
        _ => $"Filters, {activeCount} active",
    };

    private void OnFiltersFlyoutOpening(object sender, object e) => Filters?.BeginEdit();

    // The buttons run their commands; this only closes the flyout.
    private void OnFilterActionClick(object sender, RoutedEventArgs e) => FiltersFlyout.Hide();
}
