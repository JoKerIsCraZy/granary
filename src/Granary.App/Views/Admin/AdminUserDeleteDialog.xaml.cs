using Granary.Core.Api.Admin;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Views.Admin;

/// <summary>
/// Deletes an account once its name is typed, or disables it instead where the page offers that. The dialog stays open
/// while a request runs and after a failure, which it shows; it closes when the account is gone or disabled.
/// </summary>
public sealed partial class AdminUserDeleteDialog : ContentDialog
{
    private bool _running;

    public AdminUserDeleteDialog()
    {
        ViewModel = App.Services.GetRequiredService<AdminUserDeleteViewModel>();
        InitializeComponent();
        AdminDialogs.Dress(this, AdminDialogs.Default);
    }

    public AdminUserDeleteViewModel ViewModel { get; }

    /// <summary>What happened: <see cref="AdminUserDeleteOutcome.Deleted"/>, Disabled, or nothing.</summary>
    public AdminUserDeleteOutcome Outcome { get; private set; }

    /// <summary>
    /// Reads the account fresh and asks. Null when it could not be read (<see cref="AdminUserDeleteViewModel.Error"/> says
    /// why); otherwise what was done.
    /// </summary>
    public async Task<AdminUserDeleteOutcome?> AskAsync(XamlRoot root, AdminActor? actor, string id, bool offerDisable, bool viewerIsOwner)
    {
        Outcome = AdminUserDeleteOutcome.None;
        ViewModel.DisableOffered = offerDisable;
        if (!await ViewModel.StartAsync(actor, id, viewerIsOwner))
        {
            return null;
        }
        AdminInlines.Fill(ConfirmLabel, "web.admin.users.deleteDialog.confirmLabel", ("username", ViewModel.Account?.Username ?? ""));
        AdminInlines.Fill(DisableHint, "web.admin.users.deleteDialog.disableHint");
        XamlRoot = root;
        await ShowAsync();
        return Outcome;
    }

    public Visibility Shown(string text) => text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool Not(bool value) => !value;

    private void OnCancelClick(object sender, RoutedEventArgs e) => Hide();

    private async void OnDeleteClick(object sender, RoutedEventArgs e) => await RunAsync(ViewModel.DeleteAsync, AdminUserDeleteOutcome.Deleted);

    private async void OnDisableClick(object sender, RoutedEventArgs e) => await RunAsync(ViewModel.DisableAsync, AdminUserDeleteOutcome.Disabled);

    // The dialog closes only once the request went through; a failure stays on it to be read.
    private async Task RunAsync(Func<CancellationToken, Task<bool>> action, AdminUserDeleteOutcome done)
    {
        if (_running)
        {
            return;
        }
        _running = true;
        bool close = false;
        try
        {
            if (await action(CancellationToken.None))
            {
                Outcome = done;
                close = true;
            }
        }
        catch (Exception ex)
        {
            // async void above: nothing may escape. The view model words the failures it expects.
            App.Services.GetRequiredService<ILogger<AdminUserDeleteDialog>>().LogError(ex, "The account could not be deleted or disabled");
        }
        finally
        {
            _running = false;
        }
        if (close)
        {
            Hide();
        }
    }

    // Not while a request runs: its answer decides what the page shows next.
    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args) => args.Cancel = _running;

    // Read again after a conflict; the name to type is the account's name now, which another administrator may have changed.
    private async void OnReloadClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.ReloadAsync();
            AdminInlines.Fill(ConfirmLabel, "web.admin.users.deleteDialog.confirmLabel", ("username", ViewModel.Account?.Username ?? ""));
        }
        catch (Exception ex)
        {
            // async void: nothing may escape.
            App.Services.GetRequiredService<ILogger<AdminUserDeleteDialog>>().LogError(ex, "The account could not be read again");
        }
    }
}

/// <summary>What the delete dialog did to the account.</summary>
public enum AdminUserDeleteOutcome
{
    None,
    Deleted,
    Disabled,
}
