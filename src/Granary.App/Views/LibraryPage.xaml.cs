using System.ComponentModel;
using System.Globalization;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Globalization.NumberFormatting;

namespace Granary.App.Views;

/// <summary>One library; the parameter is its id. The page is cached: a visit of the same library keeps its state.</summary>
public sealed partial class LibraryPage : Page
{
    private readonly Dictionary<LibrarySortOption, RadioMenuFlyoutItem> _sortItems = [];
    private double _shelvesOffset;

    public LibraryPage()
    {
        ViewModel = App.Services.GetRequiredService<LibraryViewModel>();
        InitializeComponent();
        BuildSortMenu();
        // Years without grouping or decimals: "1999", not "1,999.00".
        var years = new DecimalFormatter { IntegerDigits = 1, FractionDigits = 0, IsGrouped = false };
        YearFromBox.NumberFormatter = years;
        YearToBox.NumberFormatter = years;

        ViewModel.LibraryChanged += (_, _) =>
        {
            _shelvesOffset = 0;
            ScrollToTop(RecommendedScroller, LibraryScroller, CollectionsScroller);
        };
        ViewModel.Browse.Grid.Restarted += (_, _) => ScrollToTop(LibraryScroller);
        ViewModel.Collections.PropertyChanged += OnCollectionsPropertyChanged;
    }

    public LibraryViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.DisplayScale = Frame?.XamlRoot?.RasterizationScale ?? XamlRoot?.RasterizationScale ?? 1;
        try
        {
            await ViewModel.ActivateAsync(e.Parameter as string);
        }
        catch (Exception ex)
        {
            // An async void handler must not throw; the page shows what it reached.
            App.Services.GetRequiredService<ILogger<LibraryPage>>().LogError(ex, "Could not show a library");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Deactivate();
        base.OnNavigatedFrom(e);
    }

    // Instance methods because x:Bind function bindings call them on the page.
    public Visibility ShowTab(bool isReady, bool isSelected) => isReady && isSelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShowBoth(bool first, bool second) => first && second ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShowFirstOnly(bool first, bool second) => first && !second ? Visibility.Visible : Visibility.Collapsed;

    public bool Not(bool value) => !value;

    public string CountText(int count) => count.ToString(CultureInfo.InvariantCulture);

    public string HeadingName(string libraryName) => libraryName.Length > 0 ? $"Library {libraryName}" : "Library";

    public string SortName(string label) => $"Sort by: {label}";

    public string OrderName(string label) => $"Order: {label}";

    public string FiltersName(int activeCount) => activeCount switch
    {
        0 => "Filters",
        1 => "Filters, 1 active",
        _ => $"Filters, {activeCount} active",
    };

    private void BuildSortMenu()
    {
        foreach (var option in ViewModel.Browse.SortOptions)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = option.Label,
                GroupName = "LibrarySort",
                Command = ViewModel.Browse.SetSortCommand,
                CommandParameter = option,
            };
            _sortItems[option] = item;
            SortMenu.Items.Add(item);
        }
    }

    private void OnSortMenuOpening(object sender, object e)
    {
        foreach (var (option, item) in _sortItems)
        {
            item.IsChecked = option == ViewModel.Browse.Sort;
        }
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse<LibraryTab>(tag, out var tab) && tab != ViewModel.SelectedTab)
        {
            ViewModel.SelectTabCommand.Execute(tab);
        }
    }

    private void OnFiltersFlyoutOpening(object sender, object e) => ViewModel.Browse.Filters.BeginEdit();

    // The buttons run their commands; this only closes the flyout.
    private void OnFilterActionClick(object sender, RoutedEventArgs e) => FiltersFlyout.Hide();

    // Opening a collection starts at the top of its titles; closing it returns to where the shelves were.
    // The focused card or Back button is hidden by the switch, so the keyboard focus moves along with it.
    private void OnCollectionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LibraryCollectionsViewModel.OpenCollection))
        {
            return;
        }
        var focused = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;
        if (ViewModel.Collections.IsCollectionOpen)
        {
            _shelvesOffset = CollectionsScroller.VerticalOffset;
            ScrollToTop(CollectionsScroller);
            if (IsWithin(focused, CollectionsScroller))
            {
                DispatcherQueue.TryEnqueue(() => BackToCollectionsButton.Focus(FocusState.Programmatic));
            }
        }
        else
        {
            bool moveFocus = ReferenceEquals(focused, BackToCollectionsButton);
            // Read when it runs: a library change resets the offset after this was queued.
            DispatcherQueue.TryEnqueue(() =>
            {
                CollectionsScroller.ChangeView(null, _shelvesOffset, null, disableAnimation: true);
                if (moveFocus)
                {
                    CollectionsTabButton.Focus(FocusState.Programmatic);
                }
            });
        }
    }

    private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }
        return false;
    }

    private static void ScrollToTop(params ScrollViewer[] scrollers)
    {
        foreach (var scroller in scrollers)
        {
            scroller.ChangeView(null, 0, null, disableAnimation: true);
        }
    }
}
