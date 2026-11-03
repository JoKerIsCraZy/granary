using System.Globalization;
using Granary.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Globalization.NumberFormatting;

namespace Granary.App.Controls;

/// <summary>The Filters pill and its flyout, for a <see cref="LibraryFiltersViewModel"/>.</summary>
public sealed partial class CatalogFiltersButton : UserControl
{
    // The sections scroll within the window's height less the heading, the buttons and some room above and below, and
    // never past the flyout's own limit, so the flyout itself never scrolls them away. The least is about two rows of
    // pills, so that only a window shorter than that and the rest together cuts the flyout.
    private const double WindowRoom = 260;
    private const double MinimumSectionsHeight = 88;
    private const double MaximumSectionsHeight = 520;

    public static readonly DependencyProperty FiltersProperty =
        DependencyProperty.Register(nameof(Filters), typeof(LibraryFiltersViewModel), typeof(CatalogFiltersButton),
            new PropertyMetadata(null, (d, _) => ((CatalogFiltersButton)d).Bindings.Update()));

    public static readonly DependencyProperty OptionsErrorTextProperty =
        DependencyProperty.Register(nameof(OptionsErrorText), typeof(string), typeof(CatalogFiltersButton),
            new PropertyMetadata("Couldn't load the genres, ratings and languages. The other filters still work."));

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

    /// <summary>What the flyout says when the genres, ratings and languages could not be read.</summary>
    public string OptionsErrorText
    {
        get => (string)GetValue(OptionsErrorTextProperty);
        set => SetValue(OptionsErrorTextProperty, value);
    }

    /// <summary>Moves focus to the Filters pill, as when the last filter badge is removed.</summary>
    public bool FocusPill(FocusState state) => PillButton.Focus(state);

    public string CountText(int count) => count.ToString(CultureInfo.InvariantCulture);

    public string FiltersName(int activeCount) => activeCount switch
    {
        0 => "Filters",
        1 => "Filters, 1 active",
        _ => $"Filters, {activeCount} active",
    };

    private void OnFiltersFlyoutOpening(object sender, object e)
    {
        double window = XamlRoot?.Size.Height ?? 0;
        FiltersScroller.MaxHeight = window > 0 ? Math.Clamp(window - WindowRoom, MinimumSectionsHeight, MaximumSectionsHeight) : MaximumSectionsHeight;
        FiltersScroller.ChangeView(null, 0, null, disableAnimation: true);
        Filters?.BeginEdit();
    }

    // The buttons run their commands; this only closes the flyout.
    private void OnFilterActionClick(object sender, RoutedEventArgs e) => FiltersFlyout.Hide();
}
