using Granary.Core.Theme;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Granary.App;

// The sidebar's top: the server's wordmark once it has loaded, its name until then and for a server without one, or when
// the picture does not load (a .webp needs Windows' WebP codec), or when the profile turned the logo off in Settings.
public sealed partial class MainWindow
{
    private readonly ServerAccent _brand;
    // What the top shows now. Any change of the sidebar's layout comes by here, and only a different picture is loaded anew,
    // so moving an entry does not flash the name.
    private Uri? _wordmarkShown;

    private void InitializeWordmark()
    {
        _brand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServerAccent.Wordmark))
            {
                ShowWordmark();
            }
        };
        Layout.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SidebarLayout.Revision))
            {
                ShowWordmark();
            }
        };
        ShowWordmark();
    }

    private void ShowWordmark()
    {
        var wordmark = Layout.ShowsServerLogo ? _brand.Wordmark : null;
        if (wordmark == _wordmarkShown)
        {
            return;
        }
        _wordmarkShown = wordmark;
        ServerNameText.Visibility = Visibility.Visible;
        if (wordmark is { } url)
        {
            ServerWordmark.Source = new BitmapImage(url);
            ServerWordmark.Visibility = Visibility.Visible;
        }
        else
        {
            ServerWordmark.Source = null;
            ServerWordmark.Visibility = Visibility.Collapsed;
        }
    }

    private void OnWordmarkOpened(object sender, RoutedEventArgs e) => ServerNameText.Visibility = Visibility.Collapsed;

    private void OnWordmarkFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _logger.LogWarning("The server's wordmark could not be shown: {Error}", e.ErrorMessage);
        ServerWordmark.Visibility = Visibility.Collapsed;
        ServerNameText.Visibility = Visibility.Visible;
    }
}
