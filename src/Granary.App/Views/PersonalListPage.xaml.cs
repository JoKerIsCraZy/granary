using System.Globalization;
using Granary.Core.Abstractions;
using Granary.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>
/// Favorites, Watchlist or History, the parameter being the list's source (<c>favorites</c>, <c>watchlist</c>, <c>history</c>);
/// or a collection's titles, the parameter being a <see cref="CollectionRouteParameter"/>. The watchlist can have two tabs.
/// </summary>
public sealed partial class PersonalListPage : Page
{
    private readonly Dictionary<ListMediaScope, RadioMenuFlyoutItem> _scopeItems = [];
    private readonly List<(LibrarySortOption? Option, RadioMenuFlyoutItem Item)> _sortItems = [];

    public PersonalListPage()
    {
        ViewModel = App.Services.GetRequiredService<PersonalListViewModel>();
        InitializeComponent();
        BuildMenus();
        ViewModel.Grid.Restarted += (_, _) => Scroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    public PersonalListViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.DisplayScale = XamlRoot?.RasterizationScale ?? 1;
        try
        {
            await (e.Parameter is CollectionRouteParameter collection
                ? ViewModel.ShowCollectionAsync(collection)
                : ViewModel.ActivateAsync(e.Parameter as string));
        }
        catch (Exception ex)
        {
            // An async void handler must not throw; failed requests are already shown by the grid.
            App.Services.GetRequiredService<ILogger<PersonalListPage>>().LogError(ex, "A list could not be shown");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Deactivate();
        base.OnNavigatedFrom(e);
    }

    // Instance methods because x:Bind function bindings call them on the page.
    public bool Not(bool value) => !value;

    public Visibility Both(bool first, bool second) => first && second ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>"Not in your library yet, 3 titles, some need attention", as the web reads the tab out.</summary>
    public string TitlesTabName(int count, bool needsAttention) => "Not in your library yet"
        + (count > 0 ? $", {count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? "title" : "titles")}" : "")
        + (needsAttention ? ", some need attention" : "");

    public string CountText(int total) => total.ToString("N0", CultureInfo.InvariantCulture);

    public string CountLabel(int total) => total == 1 ? "RESULT" : "RESULTS";

    public string CountName(int total) => total == 1 ? "1 result" : $"{CountText(total)} results";

    public string ScopeName(string label) => $"Media: {label}";

    public string SortName(string label) => $"Sort by: {label}";

    public string OrderName(string label) => $"Order: {label}";

    private void BuildMenus()
    {
        foreach (var scope in ViewModel.Scopes)
        {
            var item = new RadioMenuFlyoutItem { Text = scope.Label, GroupName = "ListScope", Command = ViewModel.SetScopeCommand, CommandParameter = scope };
            _scopeItems[scope] = item;
            ScopeMenu.Items.Add(item);
        }
        // The list's own order first; its label is the list's, so it is set when the menu opens.
        AddSort(null);
        foreach (var option in ViewModel.SortOptions)
        {
            AddSort(option);
        }
    }

    private void AddSort(LibrarySortOption? option)
    {
        var item = new RadioMenuFlyoutItem { Text = option?.Label ?? "", GroupName = "ListSort", Command = ViewModel.SetSortCommand, CommandParameter = option };
        _sortItems.Add((option, item));
        SortMenu.Items.Add(item);
    }

    private void OnLibraryTabChecked(object sender, RoutedEventArgs e) => ViewModel.ShowLibraryTabCommand.Execute(null);

    private void OnTitlesTabChecked(object sender, RoutedEventArgs e) => ViewModel.ShowTitlesTabCommand.Execute(null);

    private void OnScopeMenuOpening(object sender, object e)
    {
        foreach (var (scope, item) in _scopeItems)
        {
            item.IsChecked = scope == ViewModel.Scope;
        }
    }

    private void OnSortMenuOpening(object sender, object e)
    {
        foreach (var (option, item) in _sortItems)
        {
            if (option is null)
            {
                item.Text = ViewModel.SourceOrderLabel;
            }
            item.IsChecked = option == ViewModel.Sort;
        }
    }
}
