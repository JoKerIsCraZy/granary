using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Views.Admin;

/// <summary>
/// The admin dialogs dressed as the web's <c>DialogContent</c> (<c>components/ui/dialog.tsx</c>): p-6 (24), rounded-lg (12),
/// a hairline, the page's #141417, and a width the content fills. The title, its X and the buttons are the dialog's own
/// content, so the stock title and command row stay empty.
/// </summary>
internal static class AdminDialogs
{
    /// <summary>The web's <c>sm:max-w-lg</c>.</summary>
    public const double Default = 512;

    /// <summary>The web's <c>sm:max-w-2xl</c>, Create User's.</summary>
    public const double Wide = 672;

    public static void Dress(ContentDialog dialog, double width)
    {
        var background = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x14, 0x14, 0x17));
        dialog.Resources["ContentDialogMaxWidth"] = width;
        dialog.Resources["ContentDialogMinWidth"] = Math.Min(width, 320.0);
        dialog.Resources["ContentDialogPadding"] = new Thickness(24);
        dialog.Resources["ContentDialogBackground"] = background;
        dialog.Resources["ContentDialogTopOverlay"] = background;
        dialog.Resources["ContentDialogBorderBrush"] = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x28, 0x28, 0x2E));
        dialog.CornerRadius = new CornerRadius(12);
        dialog.BorderThickness = new Thickness(1);
    }
}

/// <summary>
/// Opens a page of the server's own web app in the browser, for the admin pages Granary does not have (Access Groups, the
/// sign-in, downloads and email settings), as the web's links lead to them. Only fixed paths of the server's address, and
/// only http or https.
/// </summary>
internal static class AdminWebLinks
{
    public const string AccessGroups = "admin/access-groups";
    public const string SignInSettings = "admin/settings/sign-in";
    public const string DownloadsSettings = "admin/settings/downloads";
    public const string EmailSettings = "admin/settings/email";
    public const string GeneralSettings = "admin/settings/general";

    public static void Open(Uri? server, string path)
    {
        if (server is { IsAbsoluteUri: true } && (server.Scheme == Uri.UriSchemeHttps || server.Scheme == Uri.UriSchemeHttp))
        {
            _ = OpenAsync(server, path);
        }
    }

    private static async Task OpenAsync(Uri server, string path)
    {
        try
        {
            var root = server.AbsoluteUri.EndsWith('/') ? server : new Uri(server.AbsoluteUri + "/");
            await Windows.System.Launcher.LaunchUriAsync(new Uri(root, path));
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Nothing opens; the page stays as it is.
        }
    }
}
