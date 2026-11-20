using Granary.Core.Theme;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Granary.App;

// The sidebar's top: the server's wordmark once it has loaded, its name until then and for a server without one, or when
// the picture does not load (a .webp needs Windows' WebP codec), or when the profile turned the logo off in Settings. The
// rail shows the server's square mark there instead, cross-faded in the same place as the web's (components/AppSidebar.tsx),
// and nothing without one: never the wordmark squeezed, and never Silo's own.
public sealed partial class MainWindow
{
    // The picture's height in the sidebar (MainWindow.xaml). Set before its address, so it never decodes any larger; the height
    // alone, as a width too would stretch it.
    private const int WordmarkDecodeHeight = 48;

    // The mark's size in the rail (MainWindow.xaml), likewise decoded by its height alone.
    private const int MarkSize = 36;

    private readonly ServerAccent _brand;
    // What the top shows now. Any change of the sidebar's layout comes by here, and only a different picture is loaded anew,
    // so moving an entry does not flash the name.
    private Uri? _wordmarkShown;
    // The picture that counts: one replaced or turned off can still report that it opened or failed.
    private BitmapImage? _wordmarkImage;
    private Uri? _markShown;
    // The mark being shown, or null without one or once it failed. A mark still loading paints nothing.
    private BitmapImage? _markImage;
    // Whether the pane is open or opening (ShowPaneParts), as the top shows it.
    private bool _brandWide = true;

    private void InitializeWordmark()
    {
        _brand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServerAccent.Wordmark))
            {
                ShowWordmark();
            }
            else if (e.PropertyName == nameof(ServerAccent.Mark))
            {
                ShowMark();
            }
        };
        Layout.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SidebarLayout.Revision))
            {
                ShowWordmark();
                ShowMark();
            }
        };
        // In the entries' icon column, which is the rail's middle.
        ServerMark.Margin = new Thickness(Math.Max(0, (Navigation.CompactPaneLength - MarkSize) / 2), 0, 0, 0);
        ShowWordmark();
        ShowMark();
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
        _wordmarkImage = null;
        if (wordmark is { } url)
        {
            var image = new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelHeight = WordmarkDecodeHeight };
            image.ImageOpened += OnWordmarkOpened;
            image.ImageFailed += OnWordmarkFailed;
            image.UriSource = url;
            _wordmarkImage = image;
            ServerWordmark.Source = image;
            ServerWordmark.Visibility = Visibility.Visible;
        }
        else
        {
            ServerWordmark.Source = null;
            ServerWordmark.Visibility = Visibility.Collapsed;
        }
    }

    private void OnWordmarkOpened(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _wordmarkImage))
        {
            ServerNameText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnWordmarkFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _wordmarkImage))
        {
            return;
        }
        _logger.LogWarning("The server's wordmark could not be shown: {Error}", e.ErrorMessage);
        ServerWordmark.Visibility = Visibility.Collapsed;
        ServerNameText.Visibility = Visibility.Visible;
    }

    // The mark keeps its place in the top while it loads, so the entries below stay put when it appears; without one the top
    // is as high as the name or wordmark alone.
    private void ShowMark()
    {
        var mark = Layout.ShowsServerLogo ? _brand.Mark : null;
        if (mark == _markShown)
        {
            return;
        }
        _markShown = mark;
        _markImage = null;
        if (mark is { } url)
        {
            var image = new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelHeight = MarkSize };
            image.ImageFailed += OnMarkFailed;
            image.UriSource = url;
            _markImage = image;
            ServerMarkBrush.ImageSource = image;
            ServerMark.Visibility = Visibility.Visible;
        }
        else
        {
            ServerMarkBrush.ImageSource = null;
            ServerMark.Visibility = Visibility.Collapsed;
        }
        ShowBrand(_brandWide);
    }

    private void OnMarkFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _markImage))
        {
            return;
        }
        _logger.LogWarning("The server's mark could not be shown: {Error}", e.ErrorMessage);
        _markImage = null;
        ServerMarkBrush.ImageSource = null;
        ServerMark.Visibility = Visibility.Collapsed;
        ShowBrand(_brandWide);
    }

    // The open pane shows the name or wordmark, the rail the mark; a rail without one shows nothing there, and the link is
    // neither clicked nor read out where nothing shows. Opacity alone, so the top keeps its height and the entries their place.
    private void ShowBrand(bool open)
    {
        _brandWide = open;
        bool hasMark = _markImage is not null;
        bool shown = open || hasMark;
        BrandWide.Opacity = open ? 1 : 0;
        ServerMark.Opacity = !open && hasMark ? 1 : 0;
        BrandLink.IsHitTestVisible = shown;
        AutomationProperties.SetAccessibilityView(BrandLink, shown ? AccessibilityView.Content : AccessibilityView.Raw);
        // The name and wordmark stay in the layout under the mark, unseen: not read out there either.
        var wide = open ? AccessibilityView.Content : AccessibilityView.Raw;
        AutomationProperties.SetAccessibilityView(ServerNameText, wide);
        AutomationProperties.SetAccessibilityView(ServerWordmark, wide);
    }
}
