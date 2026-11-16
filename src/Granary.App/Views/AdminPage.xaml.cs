using Granary.App.Controls;
using Granary.App.Views.Admin;
using Granary.Core.Abstractions;
using Granary.Core.Texts;
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
public sealed partial class AdminPage : Page, IDockSpace, IPadSections
{
    private AdminDashboardView? _dashboard;
    private AdminActivityView? _activity;
    private AdminUsersView? _users;

    public AdminPage()
    {
        Sidebar = App.Services.GetRequiredService<AdminSidebarViewModel>();
        InitializeComponent();
    }

    /// <summary>The build and the live count at the sidebar's foot and beside Activity.</summary>
    public AdminSidebarViewModel Sidebar { get; }

    // The web's section and build headings, which its CSS shows in capitals: by the rules of the language on screen.
    public string MonitorHeading => AppText.Upper(AppText.Get("web.admin.shell.nav.section.monitor"));

    public string ManageHeading => AppText.Upper(AppText.Get("web.admin.shell.nav.section.manage"));

    public string BuildHeading => AppText.Upper(AppText.Get("web.admin.shell.sidebar.build.heading"));

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
        DeactivateAll();
        _dashboard?.Release();
        _activity?.Release();
        _users?.Release();
        Bindings.StopTracking();
    }

    /// <summary>Shows one of the area's pages (<see cref="AdminPages"/>), as picking it in the sidebar does.</summary>
    public void Open(string page)
    {
        var (list, item) = Lists.SelectMany(list => list.Items.OfType<ListViewItem>().Select(item => (list, item)))
            .FirstOrDefault(entry => entry.item.Tag as string == page);
        if (item is null)
        {
            (list, item) = (Pages, (ListViewItem)Pages.Items[0]);
        }
        if (ReferenceEquals(list.SelectedItem, item))
        {
            return;
        }
        list.SelectedItem = item;
    }

    // The sidebar's sections are lists of their own that share one pick.
    private ListView[] Lists => [Pages, ManagePages];

    private async void OnPageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListView { SelectedItem: ListViewItem { Tag: string page } } picked)
        {
            return;
        }
        foreach (var list in Lists.Where(list => !ReferenceEquals(list, picked)))
        {
            list.SelectedItem = null;
        }
        DeactivateAll();
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
                case AdminPages.Users:
                    _users ??= new AdminUsersView();
                    Show(_users);
                    await _users.ActivateAsync();
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

    // A game controller's bumpers go through the area's pages.
    void IPadSections.StepSection(int by) => PadControls.StepRow(Lists, by);

    private void DeactivateAll()
    {
        _dashboard?.Deactivate();
        _activity?.Deactivate();
        _users?.Deactivate();
    }

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
