using Granary.App.Views.Admin;
using Granary.Core.Abstractions;
using Granary.Core.ViewModels.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Granary.App.Views;

/// <summary>
/// The admin area (docs/ui-design.md, Admin): its own sidebar and the page picked in it. The parameter names the page to
/// open first (<see cref="AdminPages"/>); Back into the area returns to the page last shown. Its pages switch here, not in
/// the app's history, so Back to App leaves the area in one step.
/// </summary>
public sealed partial class AdminPage : Page, IDockSpace
{
    private AdminDashboardView? _dashboard;
    private AdminActivityView? _activity;

    public AdminPage()
    {
        Sidebar = App.Services.GetRequiredService<AdminSidebarViewModel>();
        InitializeComponent();
    }

    /// <summary>The build and the live count at the sidebar's foot and beside Activity.</summary>
    public AdminSidebarViewModel Sidebar { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Back from a title opened on Activity lands on Activity again, not on the page the area first opened with.
        Open(e.NavigationMode == NavigationMode.Back ? Sidebar.LastPage : e.Parameter as string ?? AdminPages.Dashboard);
    }

    // A page left is not shown again (Back makes a new one): its views stop, and every binding lets go of the shared sidebar.
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _dashboard?.Deactivate();
        _activity?.Deactivate();
        _dashboard?.Release();
        _activity?.Release();
        Bindings.StopTracking();
    }

    private void Open(string page)
    {
        var item = Pages.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag as string == page) ?? (ListViewItem)Pages.Items[0];
        if (ReferenceEquals(Pages.SelectedItem, item))
        {
            return;
        }
        Pages.SelectedItem = item;
    }

    private async void OnPageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Pages.SelectedItem is not ListViewItem { Tag: string page })
        {
            return;
        }
        _dashboard?.Deactivate();
        _activity?.Deactivate();
        Sidebar.LastPage = page;
        try
        {
            switch (page)
            {
                case AdminPages.Activity:
                    _activity ??= new AdminActivityView();
                    Show(_activity);
                    await _activity.ActivateAsync();
                    break;
                default:
                    _dashboard ??= CreateDashboard();
                    Show(_dashboard);
                    await _dashboard.ActivateAsync();
                    break;
            }
            // What the page read itself spares the sidebar its own reads.
            await Sidebar.RefreshAsync();
        }
        catch (Exception ex)
        {
            // async void: nothing may escape. Failed reads are the page's to show; this is anything else.
            App.Services.GetRequiredService<ILogger<AdminPage>>().LogError(ex, "The admin page {Page} could not be shown", page);
        }
    }

    // The area's sidebar runs on beside the minimized player's bar, its build at the window's foot; only the page ends above it.
    void IDockSpace.ReserveBottom(double height) => PageScroller.Margin = new Thickness(0, 0, 0, height);

    private void Show(UIElement view)
    {
        PageHost.Content = view;
        PageScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private AdminDashboardView CreateDashboard()
    {
        var dashboard = new AdminDashboardView();
        dashboard.ViewModel.ActivityRequested += (_, _) => Open(AdminPages.Activity);
        return dashboard;
    }

    // Back to where the admin area was opened from, or Home.
    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        var navigation = App.Services.GetRequiredService<INavigationService>();
        if (navigation.CanGoBack)
        {
            navigation.GoBack();
        }
        else
        {
            navigation.NavigateTo(AppRoute.Home);
        }
    }
}
