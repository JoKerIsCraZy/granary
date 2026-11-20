using Granary.App.Services;
using Granary.Core.Api.Admin;
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
/// Admin → Users → Invitations. The history and its actions are the view model's; here are the dialogs: Invite someone,
/// and a row's new link. A link is shown in its dialog only, copied only through Copy link, and forgotten when it closes.
/// </summary>
public sealed partial class AdminInvitationsView : UserControl
{
    private IReadOnlyList<AdminAccessGroup> _groups = [];
    private bool _inviting;
    // Invite someone is opening or open: a second click opens nothing.
    private bool _opening;
    // Counts the tab's visits: Invite someone opens its dialog only while the visit it was asked in lasts.
    private int _visit;

    public AdminInvitationsView()
    {
        ViewModel = App.Services.GetRequiredService<AdminInvitationsViewModel>();
        Form = App.Services.GetRequiredService<AdminNewInvitationViewModel>();
        InitializeComponent();
        AdminDialogs.Dress(InviteDialog, AdminDialogs.Default, InviteRoot);
        AdminDialogs.Dress(ResendDialog, AdminDialogs.Default, ResendRoot);
        // The web's notice links to its Email settings, which open in the server's web app.
        AdminInlines.Fill(EmailSetupText, "web.admin.settings.invitations.create.emailSetup",
            () => AdminWebLinks.Open(ViewModel.Actor?.Server, AdminWebLinks.EmailSettings));
        AdminInlines.Fill(PublicUrlText, "web.admin.settings.invitations.publicUrlMissing",
            () => AdminWebLinks.Open(ViewModel.Actor?.Server, AdminWebLinks.GeneralSettings));
    }

    public AdminInvitationsViewModel ViewModel { get; }

    public AdminNewInvitationViewModel Form { get; }

    /// <summary>Reads what invitations offer and the history; the groups are Users' own, for Invite someone.</summary>
    public async Task ActivateAsync(bool viewerIsOwner, IReadOnlyList<AdminAccessGroup> groups)
    {
        _groups = groups;
        try
        {
            await ViewModel.OpenAsync(viewerIsOwner);
        }
        catch (Exception ex)
        {
            Log(ex, "The invitations could not be shown");
        }
    }

    public void Deactivate()
    {
        _visit++;
        ViewModel.Deactivate();
        Form.Clear();
        InviteDialog.Hide();
        ResendDialog.Hide();
    }

    /// <summary>The page leaves for good.</summary>
    public void Release() => Bindings.StopTracking();

    public Visibility Failed(LibraryLoadState state) => state == LibraryLoadState.Error ? Visibility.Visible : Visibility.Collapsed;

    public Visibility Unconfigured(LibraryLoadState state) => state == LibraryLoadState.Unavailable ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A pending invitation in the web's default badge, the others in its outline one.</summary>
    public static Brush StatusBackground(bool pending) => pending ? Resource("SiloPrimaryBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public static Brush StatusBorder(bool pending) => Resource(pending ? "SiloPrimaryBrush" : "SiloBorderBrush");

    public static Brush StatusForeground(bool pending) => Resource(pending ? "SiloOnPrimaryBrush" : "SiloForegroundBrush");

    /// <summary>An email whose delivery is not confirmed in the web's amber.</summary>
    public static Brush DeliveryForeground(bool unconfirmed) =>
        unconfirmed ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF5, 0x9E, 0x0B)) : Resource("SiloForegroundBrush");

    public bool EmailChoosable(bool emailDelivery, bool saving) => emailDelivery && !saving;

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    private AdminInvitationRow? RowOf(object sender) =>
        sender is FrameworkElement { Tag: string id } ? ViewModel.Rows.FirstOrDefault(row => row.Id == id) : null;

    private async void OnInviteClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanInvite || XamlRoot is null || _opening)
        {
            return;
        }
        _opening = true;
        int visit = _visit;
        try
        {
            await Form.StartAsync(ViewModel.Actor, ViewModel.Capabilities, _groups, ViewModel.ViewerIsOwner);
            if (visit != _visit || XamlRoot is null)
            {
                // The tab was left while the libraries were read.
                return;
            }
            InviteCopyResult.Text = "";
            InviteDialog.XamlRoot = XamlRoot;
            await InviteDialog.ShowAsync();
            // The link is forgotten as the dialog closes, before the history is read.
            bool made = Form.HasResult;
            Form.Clear();
            if (made)
            {
                await ViewModel.RestartAsync();
            }
        }
        catch (Exception ex)
        {
            Log(ex, "The invite dialog failed");
        }
        finally
        {
            _opening = false;
            // The link was shown once; it is not kept. A dialog of an earlier visit leaves the current one's alone.
            if (visit == _visit)
            {
                Form.Clear();
            }
        }
    }

    private void OnInviteCloseClick(object sender, RoutedEventArgs e) => InviteDialog.Hide();

    private void OnResendCloseClick(object sender, RoutedEventArgs e) => ResendDialog.Hide();

    // Invite stays on the dialog: made, it shows the link; failed, the reason and Reload history.
    private async void OnInviteSubmitClick(object sender, RoutedEventArgs e)
    {
        if (_inviting || Form.HasResult)
        {
            return;
        }
        _inviting = true;
        int visit = _visit;
        try
        {
            await Form.SaveAsync();
            if (visit != _visit)
            {
                // The tab was left while it was made: its link is not shown.
                Form.Clear();
            }
        }
        catch (Exception ex)
        {
            Log(ex, "The invitation could not be made");
        }
        finally
        {
            _inviting = false;
        }
    }

    private void OnInviteClosing(ContentDialog sender, ContentDialogClosingEventArgs args) => args.Cancel = _inviting;

    // A failed invite may still have made one: the history is read again before another try.
    private async void OnFormReloadClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RestartAsync();
            if (ViewModel.HistoryError.Length == 0)
            {
                Form.ReloadDone();
            }
        }
        catch (Exception ex)
        {
            Log(ex, "The invitation history could not be read again");
        }
    }

    private void OnCopyInviteClick(object sender, RoutedEventArgs e) => InviteCopyResult.Text = CopyText(Form.ClaimUrl);

    private void OnCopyResentClick(object sender, RoutedEventArgs e) => ResendCopyResult.Text = CopyText(ViewModel.ResentLink);

    private static string CopyText(string link) =>
        AppText.Get(link.Length > 0 && SecretClipboard.Copy(link) ? "web.admin.settings.invitations.copied" : "web.admin.settings.invitations.copyFailed");

    private void OnResendLinkClick(object sender, RoutedEventArgs e) => Resend(RowOf(sender), InvitationDeliveries.Link);

    private void OnResendEmailClick(object sender, RoutedEventArgs e) => Resend(RowOf(sender), InvitationDeliveries.Email);

    private async void Resend(AdminInvitationRow? row, string delivery)
    {
        if (row is null || XamlRoot is null)
        {
            return;
        }
        bool made = false;
        int visit = _visit;
        try
        {
            // A failed one says so with Reload history, which the viewer chooses, as on the web.
            if (!await ViewModel.ResendAsync(row, delivery))
            {
                return;
            }
            made = true;
            ResendCopyResult.Text = "";
            ResendDialog.XamlRoot = XamlRoot;
            await ResendDialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Log(ex, "The invitation could not be sent again");
        }
        finally
        {
            // The link was shown once; then the history is read again, which frees the rows. A resend of an earlier visit leaves
            // the current one's link alone: leaving the tab forgot its own.
            if (visit == _visit)
            {
                ViewModel.ForgetResent();
                if (made)
                {
                    await ReadHistoryAgainAsync();
                }
            }
        }
    }

    private async Task ReadHistoryAgainAsync()
    {
        try
        {
            await ViewModel.RestartAsync();
        }
        catch (Exception ex)
        {
            Log(ex, "The invitation history could not be read again");
        }
    }

    private async void OnRevokeClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RevokeCommand.ExecuteAsync(RowOf(sender));
        }
        catch (Exception ex)
        {
            Log(ex, "The invitation could not be revoked");
        }
    }

    private static void Log(Exception ex, string message) =>
        App.Services.GetRequiredService<ILogger<AdminInvitationsView>>().LogError(ex, "{Message}", message);
}
