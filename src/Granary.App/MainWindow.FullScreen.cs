using System.Runtime.InteropServices;
using Granary.App.Services;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace Granary.App;

// Full screen for the whole window (WindowModeService, shared with the player and a trailer): the
// title bar's Full screen left of the caption buttons; in full screen, where Windows shows none, Exit full screen and Close
// in their place; F11 anywhere. The title bar keeps its 32 px in full screen, so the pages keep their place under it.
public sealed partial class MainWindow
{
    // Windows' caption buttons as StyleCaptionButtons colours them; the title bar's own buttons take the same.
    private static readonly Color CaptionForeground = Color.FromArgb(0xFF, 0xE8, 0xE8, 0xEC);
    private static readonly Color CaptionInactiveForeground = Color.FromArgb(0xFF, 0x7C, 0x7C, 0x86);

    private const uint WmSysCommand = 0x0112;
    private const nint ScClose = 0xF060;

    private readonly WindowModeService _modes;
    private bool _isDragAreaCleared;

    private void InitializeFullScreen()
    {
        _modes.Attach(this);
        _modes.Changed += (_, _) => ShowFullScreen();
        AppTitleBar.SizeChanged += (_, _) => FollowDragArea();
        Root.Loaded += (_, _) =>
        {
            // The caption buttons' width is in pixels: it is read again as the scale or the size changes.
            Root.XamlRoot.Changed += (_, _) => PlaceWindowButtons();
            PlaceWindowButtons();
        };
        // Dimmed with the caption buttons while another window is active.
        var active = new SolidColorBrush(CaptionForeground);
        var inactive = new SolidColorBrush(CaptionInactiveForeground);
        Activated += (_, e) => FullScreenButton.Foreground = CloseButton.Foreground =
            e.WindowActivationState == WindowActivationState.Deactivated ? inactive : active;
        ShowFullScreen();
    }

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => _modes.ToggleFullScreen();

    // Windows' own Close sends this command: the window closes the same way, OnClosing and the player's stop included.
    private void OnCloseClick(object sender, RoutedEventArgs e) =>
        PostMessage(Win32Interop.GetWindowFromWindowId(AppWindow.Id), WmSysCommand, ScClose, 0);

    // F11 on any page, the player and its bar included, before any control has the key; held down, it toggles once.
    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.F11)
        {
            return;
        }
        e.Handled = true;
        if (!e.KeyStatus.WasKeyDown)
        {
            _modes.ToggleFullScreen();
        }
    }

    // Segoe Fluent Icons: FullScreen, BackToWindow, as the player's button.
    private void ShowFullScreen()
    {
        bool fullScreen = _modes.IsFullScreen;
        FullScreenGlyph.Glyph = fullScreen ? "" : "";
        string name = fullScreen ? "Exit full screen" : "Full screen";
        AutomationProperties.SetName(FullScreenButton, name);
        ToolTipService.SetToolTip(FullScreenButton, $"{name} (F11)");
        CloseButton.Visibility = fullScreen ? Visibility.Visible : Visibility.Collapsed;
        ShowWindowButtons();
    }

    // Not over the full player, which brings its own and lies over the title bar, nor over a trailer that fills the screen,
    // which leaves it by YouTube's button or Esc.
    private void ShowWindowButtons()
    {
        WindowButtons.Visibility = IsPlayerFull || _modes.IsTrailerFullScreen ? Visibility.Collapsed : Visibility.Visible;
        PlaceWindowButtons();
    }

    // Left of the caption buttons, or in the corner in full screen. In a window the drag area ends before the buttons, which
    // would otherwise be part of it and never get a click.
    private void PlaceWindowButtons()
    {
        double scale = Root.XamlRoot?.RasterizationScale ?? 1;
        double captions = _modes.IsFullScreen ? 0 : AppWindow.TitleBar.RightInset / scale;
        WindowButtons.Margin = new Thickness(0, 0, captions, 0);
        double buttons = FullScreenButton.Width + (CloseButton.Visibility == Visibility.Visible ? CloseButton.Width : 0);
        double reserved = WindowButtons.Visibility == Visibility.Visible ? captions + buttons : 0;
        AppTitleBar.Margin = new Thickness(0, 0, reserved, 0);
        // Zero wide in full screen, so that leaving it changes the drag area's size and FollowDragArea hears of it.
        AppTitleBar.Width = _modes.IsFullScreen ? 0 : double.NaN;
        FollowDragArea();
    }

    // A full-screen window has no title bar to drag, or a drag on the strip would move it: the whole window takes its clicks
    // itself, the title's strip and the player's top bar included. WinUI keeps the last drag area of an element without
    // width, so it is cleared here; back in a window WinUI does not set it again while its element has the size it had
    // before, so it is set here once from the element, as WinUI sets it. Checked by hit-testing the strip (WM_NCHITTEST).
    private void FollowDragArea()
    {
        var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        if (_modes.IsFullScreen)
        {
            source.ClearRegionRects(NonClientRegionKind.Caption);
            _isDragAreaCleared = true;
        }
        else if (_isDragAreaCleared && AppTitleBar.ActualWidth > 0 && AppTitleBar.XamlRoot is { } root)
        {
            _isDragAreaCleared = false;
            double scale = root.RasterizationScale;
            var bounds = AppTitleBar.TransformToVisual(null).TransformBounds(new Rect(0, 0, AppTitleBar.ActualWidth, AppTitleBar.ActualHeight));
            source.SetRegionRects(NonClientRegionKind.Caption, [new RectInt32((int)Math.Round(bounds.X * scale), (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale))]);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
