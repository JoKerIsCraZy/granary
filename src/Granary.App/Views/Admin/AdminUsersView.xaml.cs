using System.ComponentModel;
using Granary.Core.Texts;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Views.Admin;

/// <summary>
/// Admin → Users. The table and its search, filter, sort and pages are the view model's; here are the dialogs: Add User,
/// and Delete, which reads the account fresh before it asks, and the tabs: Users and Invitations, as the web's.
/// </summary>
public sealed partial class AdminUsersView : UserControl
{
    private bool _creating;
    private bool _active;
    private AdminInvitationsView? _invitations;

    public AdminUsersView()
    {
        ViewModel = App.Services.GetRequiredService<AdminUsersViewModel>();
        NewUser = App.Services.GetRequiredService<AdminNewUserViewModel>();
        InitializeComponent();
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

    /// <summary>The tab chosen changed: Users' own page or <see cref="AdminPages.Invitations"/>, for the sidebar to return to.</summary>
    public event EventHandler<string>? TabChanged;

    /// <summary>The Invitations tab shows.</summary>
    public bool ShowsInvitations => InvitationsTab.IsChecked == true;

    /// <summary>Reads the accounts, then the invitations when their tab shows: they need to know whether the viewer is the Owner.</summary>
    public async Task ActivateAsync()
    {
        _active = true;
        try
        {
            await ViewModel.OpenAsync();
            if (_active && ShowsInvitations)
            {
                await Invitations().ActivateAsync(ViewModel.ViewerIsOwner, ViewModel.Groups);
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

    /// <summary>Shows a tab, as the web's <c>?tab=</c> does; read on the next <see cref="ActivateAsync"/>.</summary>
    public void ShowTab(bool invitations)
    {
        _active = false;
        (invitations ? InvitationsTab : UsersTab).IsChecked = true;
    }

    public void Deactivate()
    {
        _active = false;
        ViewModel.Deactivate();
        _invitations?.Deactivate();
    }

    /// <summary>The page leaves for good.</summary>
    public void Release()
    {
        _invitations?.Release();
        Bindings.StopTracking();
    }

    public static bool Not(bool value) => !value;

    public static Visibility Hidden(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility Shown(string text) => text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static bool IsLoading(LibraryLoadState state) => state == LibraryLoadState.Loading;

    public static Visibility LoadingShown(LibraryLoadState state) => state == LibraryLoadState.Loading ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Ready(LibraryLoadState state) => state == LibraryLoadState.Ready ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>An admin's role in the web's default badge, a user's in its secondary one.</summary>
    public static Brush RoleBackground(bool isAdmin) => Resource(isAdmin ? "SiloPrimaryBrush" : "SiloSurfaceRaisedBrush");

    public static Brush RoleForeground(bool isAdmin) => Resource(isAdmin ? "SiloOnPrimaryBrush" : "SiloForegroundBrush");

    /// <summary>A group's name as a value; "No group" and an admin's dash muted, as the web's.</summary>
    public static Brush GroupForeground(bool hasGroup) => Resource(hasGroup ? "SiloForegroundBrush" : "SiloMutedBrush");

    public static object? TipOrNull(string text) => text.Length > 0 ? text : null;

    public static string SaveText(bool saving) => AppText.Get(saving ? "web.admin.users.list.create.saving" : "common.save");

    /// <summary>Shows a failure of a row's action above the table, as the web's alert line.</summary>
    internal void ShowActionError(string text)
    {
        ActionError.Text = text;
        ActionError.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    private AdminInvitationsView Invitations() => _invitations ??= new AdminInvitationsView();

    // A tab the viewer picks shows at once; while the page is not active it only shows on the next activation.
    private async void OnTabChecked(object sender, RoutedEventArgs e)
    {
        bool invitations = ReferenceEquals(sender, InvitationsTab);
        UsersPanel.Visibility = invitations ? Visibility.Collapsed : Visibility.Visible;
        InvitationsHost.Visibility = invitations ? Visibility.Visible : Visibility.Collapsed;
        if (invitations)
        {
            InvitationsHost.Content ??= Invitations();
        }
        else
        {
            _invitations?.Deactivate();
        }
        TabChanged?.Invoke(this, invitations ? AdminPages.Invitations : AdminPages.Users);
        if (!_active || !invitations)
        {
            return;
        }
        try
        {
            await Invitations().ActivateAsync(ViewModel.ViewerIsOwner, ViewModel.Groups);
        }
        catch (Exception ex)
        {
            // async void: nothing may escape.
            Log(ex, "The invitations could not be shown");
        }
    }

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

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || !ViewModel.IsAvailable || XamlRoot is null)
        {
            return;
        }
        try
        {
            ShowActionError("");
            var dialog = new AdminUserDeleteDialog();
            var outcome = await dialog.AskAsync(XamlRoot, ViewModel.Actor, id, offerDisable: false);
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
    }

    private async void OnAddUserClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsAvailable || XamlRoot is null)
        {
            return;
        }
        try
        {
            NewUser.Start(ViewModel.Actor, ViewModel.Capabilities, ViewModel.Groups, !ViewModel.GroupsFailed && ViewModel.State == LibraryLoadState.Ready,
                ViewModel.ViewerIsOwner);
            CreateDialog.XamlRoot = XamlRoot;
            if (await CreateDialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.LoadAsync();
            }
        }
        catch (Exception ex)
        {
            Log(ex, "The new account dialog failed");
        }
    }

    // Save stays on the dialog until the account is made; a failure shows on it.
    private async void OnCreateClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_creating)
        {
            return;
        }
        _creating = true;
        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = !await NewUser.SaveAsync();
        }
        catch (Exception ex)
        {
            Log(ex, "The account could not be created");
        }
        finally
        {
            _creating = false;
            deferral.Complete();
        }
    }

    private void OnCreateClosing(ContentDialog sender, ContentDialogClosingEventArgs args) => args.Cancel = _creating;

    private static void Log(Exception ex, string message) =>
        App.Services.GetRequiredService<ILogger<AdminUsersView>>().LogError(ex, "{Message}", message);
}
