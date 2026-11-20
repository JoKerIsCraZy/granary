using Granary.App.Services;
using Granary.Core.Api.Admin;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Granary.App.Views.Admin;

/// <summary>
/// Reset password for an account. The chosen way runs on its button and the dialog stays open to say what happened; a
/// link to share is copied only on request, and the link and password are forgotten when it closes.
/// </summary>
public sealed partial class AdminPasswordResetDialog : ContentDialog
{
    private bool _running;

    public AdminPasswordResetDialog()
    {
        ViewModel = App.Services.GetRequiredService<AdminPasswordResetViewModel>();
        InitializeComponent();
    }

    public AdminPasswordResetViewModel ViewModel { get; }

    public async Task AskAsync(XamlRoot root, AdminActor? actor, AdminUser account, AdminUserCapabilities capabilities, bool passwordOnly)
    {
        ViewModel.Start(actor, account, capabilities, passwordOnly);
        XamlRoot = root;
        try
        {
            await ShowAsync();
        }
        finally
        {
            ViewModel.Clear();
        }
    }

    public string PrimaryText(bool isDone, string action) => isDone ? AppText.Get("common.done") : action;

    public string CloseText(bool isDone) => isDone ? "" : AppText.Get("common.cancel");

    public bool PrimaryEnabled(bool isDone, bool canRun) => isDone || canRun;

    public bool EnabledWhen(bool can, bool canChoose) => can && canChoose;

    public Visibility ChoiceShown(bool isDone) => isDone ? Visibility.Collapsed : Visibility.Visible;

    public double PasswordOpacity(bool isPassword) => isPassword ? 1 : 0.5;

    // Done closes; otherwise the chosen way runs and the dialog stays to show its answer.
    private async void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (ViewModel.IsDone)
        {
            return;
        }
        args.Cancel = true;
        if (_running)
        {
            return;
        }
        _running = true;
        var deferral = args.GetDeferral();
        try
        {
            await ViewModel.RunAsync();
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<AdminPasswordResetDialog>>().LogError(ex, "The password could not be reset");
        }
        finally
        {
            _running = false;
            deferral.Complete();
        }
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args) => args.Cancel = _running;

    private void OnCopyClick(object sender, RoutedEventArgs e) =>
        CopyResult.Text = AppText.Get(SecretClipboard.Copy(ViewModel.ResetUrl)
            ? "web.admin.users.passwordResetDialog.linkCopied"
            : "web.admin.users.passwordResetDialog.copyFailed");
}
