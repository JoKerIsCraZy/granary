using Granary.App.Services;
using Granary.Core.ViewModels.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Granary.App.Views.Admin;

/// <summary>
/// Admin → Users → Invite Codes. The list and its actions are the view model's; here are its two dialogs (Create Invite
/// Code, Top Up), Copy through <see cref="SecretClipboard"/>, and the note's link to the web's sign-in settings.
/// </summary>
public sealed partial class AdminInviteCodesView : UserControl
{
    // The web's outline badge has no fill and the card border; its secondary one the accent fill and no border.
    private static readonly Brush Transparent = new SolidColorBrush(Colors.Transparent);
    private static readonly Brush CardBorder = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x28, 0x28, 0x2E));
    private static readonly Brush Accent = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x23, 0x23, 0x28));

    private bool _busy;

    public AdminInviteCodesView()
    {
        ViewModel = App.Services.GetRequiredService<AdminInviteCodesViewModel>();
        InitializeComponent();
        AdminDialogs.Dress(CreateDialog, AdminDialogs.Default, CreateRoot);
        AdminDialogs.Dress(TopUpDialog, AdminDialogs.Default, TopUpRoot);
        AdminInlines.Fill(SignupsOnText, "web.admin.settings.inviteCodes.signupsOn", OpenSignInSettings);
        AdminInlines.Fill(SignupsOffText, "web.admin.settings.inviteCodes.signupsOff", OpenSignInSettings);
    }

    public AdminInviteCodesViewModel ViewModel { get; }

    public Task ActivateAsync() => ViewModel.OpenAsync();

    public void Deactivate()
    {
        ViewModel.Deactivate();
        CreateDialog.Hide();
        TopUpDialog.Hide();
    }

    /// <summary>The page leaves for good.</summary>
    public void Release() => Bindings.StopTracking();

    public bool CanCreate(bool creating, bool hasCode) => !creating && hasCode;

    public static Brush LabelBrush(bool hasLabel) => Resource(hasLabel ? "SiloForegroundBrush" : "SiloMutedBrush");

    /// <summary>A code used up in the web's destructive red.</summary>
    public static Brush UsageBrush(bool usedUp) => Resource(usedUp ? "SiloDestructiveBrush" : "SiloForegroundBrush");

    public static Brush StatusFill(bool enabled) => enabled ? Transparent : Accent;

    public static Brush StatusBorder(bool enabled) => enabled ? CardBorder : Transparent;

    public static Brush MessageBrush(bool error) => Resource(error ? "SiloDestructiveBrush" : "SiloForegroundBrush");

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    private void OpenSignInSettings() => AdminWebLinks.Open(ViewModel.Actor?.Server, AdminWebLinks.SignInSettings);

    private AdminInviteCodeRow? RowOf(object sender) =>
        sender is FrameworkElement { Tag: string id } ? ViewModel.Rows.FirstOrDefault(row => row.Id == id) : null;

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.Copied(row.Code.Length > 0 && SecretClipboard.Copy(row.Code));
        }
    }

    // The switch flips the code; a switch set to the code's own state (as the row is drawn) does nothing.
    private async void OnToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || RowOf(sender) is not { } row || toggle.IsOn == row.Enabled)
        {
            return;
        }
        if (_busy)
        {
            // Another change runs: the switch goes back to the code's state, which this handler then leaves alone.
            toggle.IsOn = row.Enabled;
            return;
        }
        _busy = true;
        try
        {
            await ViewModel.ToggleAsync(row);
        }
        catch (Exception ex)
        {
            Log(ex, "The invite code could not be changed");
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || _busy)
        {
            return;
        }
        _busy = true;
        try
        {
            await ViewModel.DeleteAsync(row);
        }
        catch (Exception ex)
        {
            Log(ex, "The invite code could not be deleted");
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
        {
            return;
        }
        try
        {
            ViewModel.StartCreate();
            CreateDialog.XamlRoot = XamlRoot;
            await CreateDialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Log(ex, "The create dialog failed");
        }
    }

    private async void OnTopUpClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || XamlRoot is null)
        {
            return;
        }
        try
        {
            ViewModel.StartTopUp(row);
            TopUpDialog.XamlRoot = XamlRoot;
            await TopUpDialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Log(ex, "The top-up dialog failed");
        }
    }

    private void OnCloseCreateClick(object sender, RoutedEventArgs e) => CreateDialog.Hide();

    private void OnCloseTopUpClick(object sender, RoutedEventArgs e) => TopUpDialog.Hide();

    private void OnCreateSubmitClick(object sender, RoutedEventArgs e) => SubmitCreate();

    private void OnTopUpSubmitClick(object sender, RoutedEventArgs e) => SubmitTopUp();

    // Enter in a field submits, as the web's form.
    private void OnCreateKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && e.OriginalSource is TextBox)
        {
            e.Handled = true;
            SubmitCreate();
        }
    }

    private void OnTopUpKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && e.OriginalSource is TextBox)
        {
            e.Handled = true;
            SubmitTopUp();
        }
    }

    private async void SubmitCreate()
    {
        try
        {
            if (await ViewModel.CreateAsync())
            {
                CreateDialog.Hide();
            }
        }
        catch (Exception ex)
        {
            Log(ex, "The invite code could not be created");
        }
    }

    private async void SubmitTopUp()
    {
        try
        {
            if (await ViewModel.TopUpAsync())
            {
                TopUpDialog.Hide();
            }
        }
        catch (Exception ex)
        {
            Log(ex, "The invite code could not be topped up");
        }
    }

    // A dialog stays open while what it sent is on its way.
    private void OnDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args) =>
        args.Cancel = ViewModel.IsCreating || ViewModel.IsToppingUp;

    private static void Log(Exception ex, string message) =>
        App.Services.GetRequiredService<ILogger<AdminInviteCodesView>>().LogError(ex, "{Message}", message);
}
