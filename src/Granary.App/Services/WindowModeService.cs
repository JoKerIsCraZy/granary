using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Granary.App.Services;

/// <summary>
/// The window's one full-screen state, shared by the app (the title bar's button, F11), the player (F, its button, the
/// mini view) and a trailer's own full screen. Core's <see cref="WindowModes"/> decides; this puts it on the window through
/// its AppWindow presenters and keeps the window as it was to go back to (<see cref="WindowedPlacement"/>), and to open
/// there at the next start (<see cref="WindowPlaceFile"/>).
/// </summary>
public sealed partial class WindowModeService(ILogger<WindowModeService> logger, WindowPlaceFile placeFile)
{
    // The mini view's width in view pixels; 16:9, as the web's picture-in-picture window starts.
    private const double MiniViewWidth = 480;

    private readonly WindowModes _modes = new();
    private readonly WindowedPlacement _windowed = new(logger);
    private Window? _window;

    /// <summary>The mode changed, or a trailer's full screen began or ended.</summary>
    public event EventHandler? Changed;

    public bool IsFullScreen => _modes.IsFullScreen;

    public bool IsMiniView => _modes.IsMiniView;

    /// <summary>A trailer's page fills the screen.</summary>
    public bool IsTrailerFullScreen => _modes.IsTrailerFullScreen;

    private double Scale => _window?.Content?.XamlRoot?.RasterizationScale ?? 1;

    /// <summary>The main window, whose modes these are; MainWindow attaches itself as it is made.</summary>
    public void Attach(Window window)
    {
        _window = window;
        window.AppWindow.Changed += OnAppWindowChanged;
        AttachPlace(window);
    }

    public void ToggleFullScreen() => Change(_modes.ToggleFullScreen);

    public void SetFullScreen(bool fullScreen) => Change(() => _modes.SetFullScreen(fullScreen));

    public void SetMiniView(bool miniView) => Change(() => _modes.SetMiniView(miniView));

    public void SetTrailerFullScreen(bool fullScreen) => Change(() => _modes.SetTrailerFullScreen(fullScreen));

    private void Change(Action decide)
    {
        var from = _modes.Mode;
        bool trailer = _modes.IsTrailerFullScreen;
        decide();
        if (_modes.Mode != from && _window?.AppWindow is { } window)
        {
            try
            {
                Present(window, from, _modes.Mode);
            }
            catch (Exception ex)
            {
                // The state must not say what the window does not show: it follows what the window shows instead.
                logger.LogWarning(ex, "The window did not go from {From} to {To}", from, _modes.Mode);
                _modes.Follow(ShownMode(window));
            }
        }
        if (_modes.Mode != from || _modes.IsTrailerFullScreen != trailer)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Present(AppWindow window, WindowMode from, WindowMode to)
    {
        if (from == WindowMode.Window)
        {
            // Kept to go back to: maximized, or restored with its place and size. Full screen and the mini view go to each
            // other without it, so leaving either still finds the window as it was before both.
            KeepPlace(window);
        }
        switch (to)
        {
            case WindowMode.FullScreen:
                window.SetPresenter(AppWindowPresenterKind.FullScreen);
                break;
            case WindowMode.MiniView:
                // The web player's Picture in Picture: the window itself, small and above the others.
                var presenter = CompactOverlayPresenter.Create();
                presenter.InitialSize = CompactOverlaySize.Medium;
                window.SetPresenter(presenter);
                // The medium size alone keeps the window's shape.
                window.Resize(new SizeInt32((int)Math.Round(MiniViewWidth * Scale), (int)Math.Round(MiniViewWidth * 9 / 16 * Scale)));
                break;
            default:
                _windowed.Restore(window, Scale);
                break;
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        var mode = ModeOf(sender.Presenter.Kind);
        if (mode == WindowMode.Window && _modes.Mode == WindowMode.Window
            && (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange))
        {
            // Kept as it goes, so a full screen or mini view the window takes by itself still returns to it.
            KeepPlace(sender);
        }
        // A presenter the window took by itself; the ones set here match the state already.
        if (args.DidPresenterChange && mode != _modes.Mode)
        {
            _modes.Follow(mode);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // What the window shows; a window that is closing may not say any more, and counts as a window.
    private static WindowMode ShownMode(AppWindow window)
    {
        try
        {
            return ModeOf(window.Presenter.Kind);
        }
        catch (Exception)
        {
            return WindowMode.Window;
        }
    }

    private static WindowMode ModeOf(AppWindowPresenterKind kind) => kind switch
    {
        AppWindowPresenterKind.FullScreen => WindowMode.FullScreen,
        AppWindowPresenterKind.CompactOverlay => WindowMode.MiniView,
        _ => WindowMode.Window,
    };
}
