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
    }

    public AdminUserDeleteViewModel ViewModel { get; }

    /// <summary>What happened: <see cref="AdminUserDeleteOutcome.Deleted"/>, Disabled, or nothing.</summary>
    public AdminUserDeleteOutcome Outcome { get; private set; }

    /// <summary>
    /// Reads the account fresh and asks. Null when it could not be read (<see cref="AdminUserDeleteViewModel.Error"/> says
    /// why); otherwise what was done.
    /// </summary>
    public async Task<AdminUserDeleteOutcome?> AskAsync(XamlRoot root, AdminActor? actor, string id, bool offerDisable)
    {
        Outcome = AdminUserDeleteOutcome.None;
        ViewModel.DisableOffered = offerDisable;
        if (!await ViewModel.StartAsync(actor, id))
        {
            return null;
        }
        AdminInlines.Fill(ConfirmLabel, "web.admin.users.deleteDialog.confirmLabel", ("username", ViewModel.Account?.Username ?? ""));
        AdminInlines.Fill(DisableHint, "web.admin.users.deleteDialog.disableHint");
        XamlRoot = root;
        await ShowAsync();
        return Outcome;
    }

    public string SecondaryText(bool offersDisable) => offersDisable ? AppText.Get("web.admin.users.deleteDialog.disableInstead") : "";

    public Visibility Shown(string text) => text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void OnDeleteClick(ContentDialog sender, ContentDialogButtonClickEventArgs args) =>
        await RunAsync(args, ViewModel.DeleteAsync, AdminUserDeleteOutcome.Deleted);

    private async void OnDisableClick(ContentDialog sender, ContentDialogButtonClickEventArgs args) =>
        await RunAsync(args, ViewModel.DisableAsync, AdminUserDeleteOutcome.Disabled);

    // The dialog closes only once the request went through; a failure stays on it to be read.
    private async Task RunAsync(ContentDialogButtonClickEventArgs args, Func<CancellationToken, Task<bool>> action, AdminUserDeleteOutcome done)
    {
        args.Cancel = true;
        if (_running)
        {
            return;
        }
        _running = true;
        var deferral = args.GetDeferral();
        try
        {
            if (await action(CancellationToken.None))
            {
                Outcome = done;
                args.Cancel = false;
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
            deferral.Complete();
        }
    }

    // Not while a request runs: its answer decides what the page shows next.
    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args) => args.Cancel = _running;

    private async void OnReloadClick(object sender, RoutedEventArgs e) => await ViewModel.ReloadAsync();
}

/// <summary>What the delete dialog did to the account.</summary>
public enum AdminUserDeleteOutcome
{
    None,
    Deleted,
    Disabled,
}
