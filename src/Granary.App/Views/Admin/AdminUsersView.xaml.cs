using System.ComponentModel;
using Granary.Core.Texts;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Granary.App.Views.Admin;

/// <summary>
/// Admin → Users. The table and its search, filter, sort and pages are the view model's; here are the dialogs: Add User,
/// and Delete, which reads the account fresh before it asks; the header's links to the web's pages Granary does not have;
/// and the tabs: Users, Invitations and Invite Codes, as the web's.
/// </summary>
public sealed partial class AdminUsersView : UserControl
{
    private bool _creating;
    // A delete dialog is opening or open: a second click opens nothing.
    private bool _deleting;
    private bool _active;
    private AdminInvitationsView? _invitations;
    private AdminInviteCodesView? _inviteCodes;

    public AdminUsersView()
    {
        ViewModel = App.Services.GetRequiredService<AdminUsersViewModel>();
        NewUser = App.Services.GetRequiredService<AdminNewUserViewModel>();
        InitializeComponent();
        // The body holds 440 px while the window has room; the title, checkbox, tabs and Save take about 220 more.
        AdminDialogs.Dress(CreateDialog, AdminDialogs.Wide, CreateForm, CreateBody, bodyHeight: 440, reserve: 220);
        foreach (int size in AdminUsersViewModel.PageSizes)
        {
            PageSizeBox.Items.Add(new ComboBoxItem { Content = ViewModel.PageSizeText(size), Tag = size });
        }
        PageSizeBox.SelectedIndex = 0;
        ViewModel.PropertyChanged += OnViewModelChanged;
        ShowSort();
        UsersTab.IsChecked = true;
    }

    public AdminUsersViewModel ViewModel { get; }

    public AdminNewUserViewModel NewUser { get; }

    /// <summary>Asks the page to open an account.</summary>
    public event EventHandler<string>? UserRequested
    {
        add => ViewModel.UserRequested += value;
        remove => ViewModel.UserRequested -= value;
    }

    /// <summary>The tab chosen changed (<see cref="Tab"/>), for the sidebar to return to.</summary>
    public event EventHandler<string>? TabChanged;

    /// <summary>The tab shown: <see cref="AdminPages.Users"/>, <see cref="AdminPages.Invitations"/> or <see cref="AdminPages.InviteCodes"/>.</summary>
    public string Tab => InvitationsTab.IsChecked == true ? AdminPages.Invitations
        : InviteCodesTab.IsChecked == true ? AdminPages.InviteCodes
        : AdminPages.Users;

    /// <summary>Reads the accounts, then the tab shown: invitations need to know whether the viewer is the Owner.</summary>
    public async Task ActivateAsync()
    {
        _active = true;
        try
        {
            await ViewModel.OpenAsync();
            if (_active)
            {
                await ActivateTabAsync(Tab);
            }
        }
        catch (Exception ex)
        {
            Log(ex, "The users could not be shown");
        }
    }

    /// <summary>
    /// Back from an account: the list read again as the account and profile it was opened as, not as whoever is signed in now,
    /// as the account's page acted.
    /// </summary>
    public async Task ReturnAsync()
    {
        _active = true;
        try
        {
            await ViewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            Log(ex, "The users could not be shown");
        }
    }

    /// <summary>Shows a tab (<see cref="AdminPages"/>), as the web's <c>?tab=</c> does; read on the next <see cref="ActivateAsync"/>.</summary>
    public void ShowTab(string tab)
    {
        _active = false;
        (tab switch
        {
            AdminPages.Invitations => InvitationsTab,
            AdminPages.InviteCodes => InviteCodesTab,
            _ => UsersTab,
        }).IsChecked = true;
    }

    public void Deactivate()
    {
        _active = false;
        ViewModel.Deactivate();
        _invitations?.Deactivate();
        _inviteCodes?.Deactivate();
        CreateDialog.Hide();
    }

    /// <summary>The page leaves for good.</summary>
    public void Release()
    {
        _invitations?.Release();
        _inviteCodes?.Release();
        Bindings.StopTracking();
    }

    public static bool Not(bool value) => !value;

    public static Visibility Hidden(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility Shown(string text) => text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static bool IsLoading(LibraryLoadState state) => state == LibraryLoadState.Loading;

    public static Visibility LoadingShown(LibraryLoadState state) => state == LibraryLoadState.Loading ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Ready(LibraryLoadState state) => state == LibraryLoadState.Ready ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Create User's tab <paramref name="tab"/> is the one picked.</summary>
    public static bool IsTab(int selected, int tab) => selected == tab;

    public static Visibility TabShown(int selected, int tab) => selected == tab ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>An admin's role in the web's default badge, a user's in its secondary one.</summary>
    public static Brush RoleBackground(bool isAdmin) => Resource(isAdmin ? "SiloPrimaryBrush" : "SiloSurfaceRaisedBrush");

    public static Brush RoleForeground(bool isAdmin) => Resource(isAdmin ? "SiloOnPrimaryBrush" : "SiloForegroundBrush");

    /// <summary>A group's name as a value; "No group" and an admin's dash muted, as the web's.</summary>
    public static Brush GroupForeground(bool hasGroup) => Resource(hasGroup ? "SiloForegroundBrush" : "SiloMutedBrush");

    public static object? TipOrNull(string text) => text.Length > 0 ? text : null;

    /// <summary>The header's badge, for a screen reader: "Public signups on" or "… off".</summary>
    public string SignupsText(bool on) => AppText.Get(on ? "web.admin.users.list.signupsOn" : "web.admin.users.list.signupsOff");

    /// <summary>Shows a failure of a row's action above the table, as the web's alert line.</summary>
    internal void ShowActionError(string text)
    {
        ActionError.Text = text;
        ActionError.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    private AdminInvitationsView Invitations() => _invitations ??= new AdminInvitationsView();

    private AdminInviteCodesView InviteCodes() => _inviteCodes ??= new AdminInviteCodesView();

    // A tab the viewer picks shows at once; while the page is not active it only shows on the next activation.
    private async void OnTabChecked(object sender, RoutedEventArgs e)
    {
        string tab = Tab;
        UsersPanel.Visibility = tab == AdminPages.Users ? Visibility.Visible : Visibility.Collapsed;
        InvitationsHost.Visibility = tab == AdminPages.Invitations ? Visibility.Visible : Visibility.Collapsed;
        InviteCodesHost.Visibility = tab == AdminPages.InviteCodes ? Visibility.Visible : Visibility.Collapsed;
        if (tab == AdminPages.Invitations)
        {
            InvitationsHost.Content ??= Invitations();
        }
        else
        {
            _invitations?.Deactivate();
        }
        if (tab == AdminPages.InviteCodes)
        {
            InviteCodesHost.Content ??= InviteCodes();
        }
        else
        {
            _inviteCodes?.Deactivate();
        }
        TabChanged?.Invoke(this, tab);
        if (!_active)
        {
            return;
        }
        try
        {
            await ActivateTabAsync(tab);
        }
        catch (Exception ex)
        {
            // async void: nothing may escape.
            Log(ex, "The tab could not be shown");
        }
    }

    private Task ActivateTabAsync(string tab) => tab switch
    {
        AdminPages.Invitations => Invitations().ActivateAsync(ViewModel.ViewerIsOwner, ViewModel.Groups),
        AdminPages.InviteCodes => InviteCodes().ActivateAsync(),
        _ => Task.CompletedTask,
    };

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AdminUsersViewModel.SortField) or nameof(AdminUsersViewModel.SortDescending))
        {
            ShowSort();
        }
    }

    // The chevron of the column that sorts, up or down as its order; the others' hidden, as the web's.
    private void ShowSort()
    {
        (AdminUserSort Field, FontIcon Icon)[] chevrons =
        [
            (AdminUserSort.Username, UsernameChevron), (AdminUserSort.Email, EmailChevron), (AdminUserSort.Role, RoleChevron),
            (AdminUserSort.Status, StatusChevron), (AdminUserSort.Created, CreatedChevron), (AdminUserSort.LastActive, LastActiveChevron),
        ];
        foreach (var (field, icon) in chevrons)
        {
            bool active = field == ViewModel.SortField;
            icon.Opacity = active ? 1 : 0;
            icon.Glyph = active && !ViewModel.SortDescending ? "" : "";
        }
    }

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<AdminUserSort>(tag, out var field))
        {
            ViewModel.SortByCommand.Execute(field);
        }
    }

    private void OnPageSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageSizeBox.SelectedItem is ComboBoxItem { Tag: int size })
        {
            ViewModel.PageSize = size;
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            ViewModel.OpenUserCommand.Execute(ViewModel.Rows.FirstOrDefault(row => row.Id == id));
        }
    }

    // The web's badge links to its sign-in settings, and Access Groups to its page: Granary has neither, so the server's web
    // app opens them.
    private void OnSignupsClick(object sender, RoutedEventArgs e) => AdminWebLinks.Open(ViewModel.Actor?.Server, AdminWebLinks.SignInSettings);

    private void OnAccessGroupsClick(object sender, RoutedEventArgs e) => AdminWebLinks.Open(ViewModel.Actor?.Server, AdminWebLinks.AccessGroups);

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || !ViewModel.IsAvailable || XamlRoot is null || _deleting)
        {
            return;
        }
        _deleting = true;
        try
        {
            ShowActionError("");
            var dialog = new AdminUserDeleteDialog();
            var outcome = await dialog.AskAsync(XamlRoot, ViewModel.Actor, id, offerDisable: false, ViewModel.ViewerIsOwner);
            if (outcome is null)
            {
                ShowActionError(dialog.ViewModel.Error);
            }
            else if (outcome != AdminUserDeleteOutcome.None)
            {
                await ViewModel.LoadAsync();
            }
        }
        catch (Exception ex)
        {
            Log(ex, "The account could not be deleted");
        }
        finally
        {
            _deleting = false;
        }
    }

    private async void OnAddUserClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsAvailable || XamlRoot is null)
        {
            return;
        }
        try
        {
            bool made = false;
            NewUser.Start(ViewModel.Actor, ViewModel.Capabilities, ViewModel.Groups, !ViewModel.GroupsFailed && ViewModel.State == LibraryLoadState.Ready,
                ViewModel.ViewerIsOwner);
            AccessFields.Server = ViewModel.Actor?.Server;
            CreateDialog.XamlRoot = XamlRoot;
            _createdAccount = false;
            var read = NewUser.ReadAsync();
            await CreateDialog.ShowAsync();
            made = _createdAccount;
            await read;
            if (made)
            {
                await ViewModel.LoadAsync();
            }
        }
        catch (Exception ex)
        {
            Log(ex, "The new account dialog failed");
        }
    }

    // The account was made: the dialog closed on it, and the list is read again.
    private bool _createdAccount;

    private void OnCreateTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int tab))
        {
            NewUser.SelectedTab = tab;
        }
    }

    private void OnCreateCloseClick(object sender, RoutedEventArgs e) => CreateDialog.Hide();

    // Enter in a one-line field saves, as the web's form submits.
    private void OnCreateKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && e.OriginalSource is TextBox { AcceptsReturn: false } or PasswordBox)
        {
            e.Handled = true;
            SaveNewUser();
        }
    }

    private void OnCreateSaveClick(object sender, RoutedEventArgs e) => SaveNewUser();

    // Save stays on the dialog until the account is made; a failure shows on it, an invalid limit on its tab.
    private async void SaveNewUser()
    {
        if (_creating)
        {
            return;
        }
        _creating = true;
        try
        {
            if (await NewUser.SaveAsync())
            {
                _createdAccount = true;
                _creating = false;
                CreateDialog.Hide();
            }
        }
        catch (Exception ex)
        {
            Log(ex, "The account could not be created");
        }
        finally
        {
            _creating = false;
        }
    }

    private void OnCreateClosing(ContentDialog sender, ContentDialogClosingEventArgs args) => args.Cancel = _creating;

    // The password is sent once and kept nowhere else: closed without saving, it is dropped too.
    private void OnCreateClosed(ContentDialog sender, ContentDialogClosedEventArgs args) => NewUser.Password = "";

    private static void Log(Exception ex, string message) =>
        App.Services.GetRequiredService<ILogger<AdminUsersView>>().LogError(ex, "{Message}", message);
}
