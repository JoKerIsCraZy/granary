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

    /// <summary>The window height a dialog leaves: its padding, border and the room WinUI keeps around it.</summary>
    private const double Chrome = 112;

    /// <summary>The scrollbar's gutter: the body's right padding, and the dialog's right padding that much smaller.</summary>
    public const double Gutter = 16;

    /// <summary>
    /// Dresses <paramref name="dialog"/> as the web's and keeps <paramref name="root"/> (its title, a body that scrolls and
    /// its footer) within the window, fitted again when the window is resized, so the title and the buttons always show.
    /// <paramref name="body"/>, when given, is held at <paramref name="bodyHeight"/> while the window has room (so the dialog
    /// does not change height between tabs), less <paramref name="reserve"/> for the rest of the dialog when it has not.
    /// </summary>
    public static void Dress(ContentDialog dialog, double width, FrameworkElement root, FrameworkElement? body = null, double bodyHeight = 0,
        double reserve = 0)
    {
        Dress(dialog, width);
        void Fit()
        {
            if (dialog.XamlRoot is not { } xamlRoot)
            {
                return;
            }
            double available = Math.Max(160, xamlRoot.Size.Height - Chrome);
            root.MaxHeight = available;
            if (body is not null && bodyHeight > 0)
            {
                body.Height = Math.Max(100, Math.Min(bodyHeight, available - reserve));
            }
        }
        void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Fit();
        dialog.Opened += (sender, _) =>
        {
            Fit();
            sender.XamlRoot.Changed += OnRootChanged;
        };
        dialog.Closed += (sender, _) =>
        {
            if (sender.XamlRoot is { } xamlRoot)
            {
                xamlRoot.Changed -= OnRootChanged;
            }
        };
    }

    public static void Dress(ContentDialog dialog, double width)
    {
        var background = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x14, 0x14, 0x17));
        dialog.Resources["ContentDialogMaxWidth"] = width;
        dialog.Resources["ContentDialogMinWidth"] = Math.Min(width, 320.0);
        // The right side is the body's gutter short, so the scrollbar sits in the gutter and the text 24 px from the edge.
        dialog.Resources["ContentDialogPadding"] = new Thickness(24, 24, 24 - Gutter, 24);
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
            // The server's address without a query, fragment or user part, its path ending in a slash, then the page's.
            var builder = new UriBuilder(server) { Query = "", Fragment = "", UserName = "", Password = "" };
            builder.Path = (builder.Path.EndsWith('/') ? builder.Path : builder.Path + "/") + path;
            await Windows.System.Launcher.LaunchUriAsync(builder.Uri);
        }
        catch (Exception ex)
        {
            // Nothing opens; the page stays as it is. The address is the user's own, so only the kind of failure is noted.
            System.Diagnostics.Debug.WriteLine($"A web page of the server could not be opened: {ex.GetType().Name}");
        }
    }
}
