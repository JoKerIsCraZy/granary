using Granary.Core.Api.Admin;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Views.Admin;

/// <summary>
/// One account in Admin → Users. The cards and actions are the view model's; here are the dialogs (Reset password, Delete)
/// and the way back to the list, which the breadcrumb, All users and a deleted account take.
/// </summary>
public sealed partial class AdminUserDetailView : UserControl
{
    public AdminUserDetailView()
    {
        ViewModel = App.Services.GetRequiredService<AdminUserDetailViewModel>();
        Sessions = App.Services.GetRequiredService<AdminUserSessionsViewModel>();
        InitializeComponent();
        ViewModel.Closed += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Back to the list of accounts.</summary>
    public event EventHandler? BackRequested;

    public AdminUserDetailViewModel ViewModel { get; }

    public AdminUserSessionsViewModel Sessions { get; }

    /// <summary>Opens the account with what the list read, as the account and profile the list was opened as.</summary>
    public async Task OpenAsync(AdminActor? actor, string id, AdminUserCapabilities capabilities, IReadOnlyList<AdminAccessGroup> groups,
        bool viewerIsOwner)
    {
        try
        {
            await ViewModel.OpenAsync(actor, id, capabilities, groups, viewerIsOwner);
            await OpenSessionsAsync();
        }
        catch (Exception ex)
        {
            Log(ex, "The account could not be shown");
        }
    }

    // The account's sign-ins, once the account itself was read: after opening, or after a retry that read it.
    private async Task OpenSessionsAsync()
    {
        if (ViewModel.Capabilities.LoginSessions && SessionsCard.Visibility != Visibility.Visible && ViewModel.Account is { } account
            && ViewModel.Header is { } header)
        {
            SessionsCard.Visibility = Visibility.Visible;
            await Sessions.OpenAsync(ViewModel.Actor, account, header.Manageable && ViewModel.Capabilities.Available);
        }
    }

    public void Deactivate() => ViewModel.Deactivate();

    public void Release() => Bindings.StopTracking();

    public static string NameOf(AdminUserHeader? header) => header?.Username ?? "";

    public static string Initial(AdminUserHeader? header) => header is null ? "" : AdminText.Initial(header.Username);

    public static string RoleOf(AdminUserHeader? header) => header?.RoleText ?? "";

    public static string StatusOf(AdminUserHeader? header) => header?.StatusText ?? "";

    public static string FactsOf(AdminUserHeader? header) => header?.Facts ?? "";

    public static string ViewOnlyText(AdminUserHeader? header) => header?.ViewOnlyNotice ?? "";

    public static Brush RoleBackground(AdminUserHeader? header) => AdminUsersView.RoleBackground(header?.IsAdmin == true);

    public static Brush RoleForeground(AdminUserHeader? header) => AdminUsersView.RoleForeground(header?.IsAdmin == true);

    public static Brush StatusBorder(AdminUserHeader? header) => header is { IsEnabled: false }
        ? AdminDashboardView.StatusBorder(false) : (Brush)Application.Current.Resources["SiloSuccessBrush"];

    public static Brush StatusBackground(AdminUserHeader? header) => AdminDashboardView.StatusBackground(header?.IsEnabled != false);

    public static Visibility Temporary(AdminUserHeader? header) => Show(header?.HasTemporaryPassword == true);

    public static Visibility ViewOnly(AdminUserHeader? header) => Show(header?.IsViewOnly == true);

    public static Visibility Disabled(AdminUserHeader? header) => Show(header is { IsEnabled: false });

    public static Visibility Enableable(AdminUserHeader? header) => Show(header?.CanEnable == true);

    public static Visibility Resettable(AdminUserHeader? header) => Show(header?.ShowsReset == true);

    public static Visibility PasswordSettable(AdminUserHeader? header) => Show(header?.ShowsSetPassword == true);

    public static Visibility HasMenu(AdminUserHeader? header) => Show(header?.HasMenu == true);

    /// <summary>The web's note for an admin the viewer manages but may not change the access of.</summary>
    public static Visibility OwnerOnlyNote(AdminUserHeader? header) => Show(header is { Manageable: true, PolicyManageable: false });

    public static bool HasText(string text) => text.Length > 0;

    public static Visibility Unavailable(AdminUserCapabilities capabilities) => Show(!capabilities.Available);

    public static Visibility Failed(LibraryLoadState state) => Show(state == LibraryLoadState.Error);

    public static Visibility Gone(LibraryLoadState state) => Show(state == LibraryLoadState.Unavailable);

    public static Visibility NotAdmin(bool isAdmin) => Show(!isAdmin);

    private static Visibility Show(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private void OnUsersClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.ReloadAsync();
            await OpenSessionsAsync();
        }
        catch (Exception ex)
        {
            Log(ex, "The account could not be read again");
        }
    }

    // Only what the viewer may do, as the web's menu: Make owner, Disable or Enable, then Delete.
    private void OnMenuOpening(object? sender, object e)
    {
        var header = ViewModel.Header;
        MakeOwnerItem.Visibility = Show(header?.CanTransfer == true);
        DisableItem.Visibility = Show(header?.CanDisable == true);
        EnableItem.Visibility = Show(header?.CanEnable == true);
        DeleteItem.Visibility = Show(header?.CanDelete == true);
        DeleteSeparator.Visibility = Show(header is { CanDelete: true } && (header.CanTransfer || header.CanDisable || header.CanEnable));
        bool available = ViewModel.Capabilities.Available;
        MakeOwnerItem.IsEnabled = DisableItem.IsEnabled = EnableItem.IsEnabled = DeleteItem.IsEnabled = available;
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Account is not { } account || XamlRoot is null)
        {
            return;
        }
        try
        {
            var dialog = new AdminUserDeleteDialog();
            var outcome = await dialog.AskAsync(XamlRoot, ViewModel.Actor, account.Id, offerDisable: true);
            if (outcome is null)
            {
                ViewModel.Say(dialog.ViewModel.Error, isError: true);
            }
            else if (outcome != AdminUserDeleteOutcome.None)
            {
                await ViewModel.DeletedOrDisabledAsync(outcome == AdminUserDeleteOutcome.Deleted);
            }
        }
        catch (Exception ex)
        {
            Log(ex, "The account could not be deleted");
        }
    }

    private void OnResetClick(object sender, RoutedEventArgs e) => ShowReset(passwordOnly: false);

    private void OnSetPasswordClick(object sender, RoutedEventArgs e) => ShowReset(passwordOnly: true);

    private async void ShowReset(bool passwordOnly)
    {
        if (ViewModel.Account is not { } account || XamlRoot is null || !ViewModel.Capabilities.Available)
        {
            return;
        }
        try
        {
            var dialog = new AdminPasswordResetDialog();
            await dialog.AskAsync(XamlRoot, ViewModel.Actor, account, ViewModel.Capabilities, passwordOnly);
            // A temporary password or a reset changes what the header says of it.
            await ViewModel.ReloadAsync();
        }
        catch (Exception ex)
        {
            Log(ex, "The password could not be reset");
        }
    }

    private async void OnSignOutSessionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id })
        {
            return;
        }
        try
        {
            await Sessions.SignOutCommand.ExecuteAsync(Sessions.Sessions.FirstOrDefault(row => row.Id == id));
        }
        catch (Exception ex)
        {
            Log(ex, "The session could not be signed out");
        }
    }

    private static void Log(Exception ex, string message) =>
        App.Services.GetRequiredService<ILogger<AdminUserDetailView>>().LogError(ex, "{Message}", message);
}
