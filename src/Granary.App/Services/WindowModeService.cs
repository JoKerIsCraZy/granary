using Granary.Core.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Granary.App.Services;

/// <summary>
/// The window's one full-screen state, shared by the app (the title bar's button, F11), the player (F, its button, the
/// mini view) and a trailer's own full screen. Core's <see cref="WindowModes"/> decides; this puts it on the window through
/// its AppWindow presenters and keeps the window as it was to go back to (<see cref="WindowedPlacement"/>).
/// </summary>
public sealed class WindowModeService
{
    // The mini view's width in view pixels; 16:9, as the web's picture-in-picture window starts.
    private const double MiniViewWidth = 480;

    private readonly WindowModes _modes = new();
    private readonly WindowedPlacement _windowed = new();
    private Window? _window;

    /// <summary>The mode changed, or a trailer's full screen began or ended.</summary>
    public event EventHandler? Changed;

    public bool IsFullScreen => _modes.IsFullScreen;

    public bool IsMiniView => _modes.IsMiniView;

    /// <summary>A trailer's page fills the screen.</summary>
    public bool IsTrailerFullScreen => _modes.IsTrailerFullScreen;

    /// <summary>The main window, whose modes these are; MainWindow attaches itself as it is made.</summary>
    public void Attach(Window window)
    {
        _window = window;
        window.AppWindow.Changed += OnAppWindowChanged;
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
        if (_modes.Mode != from)
        {
            Present(from, _modes.Mode);
        }
        if (_modes.Mode != from || _modes.IsTrailerFullScreen != trailer)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Present(WindowMode from, WindowMode to)
    {
        if (_window?.AppWindow is not { } window)
        {
            return;
        }
        if (from == WindowMode.Window)
        {
            // Kept to go back to: maximized, or restored with its place and size. Full screen and the mini view go to each
            // other without it, so leaving either still finds the window as it was before both.
            _windowed.Keep(window);
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
                double scale = _window.Content?.XamlRoot?.RasterizationScale ?? 1;
                window.Resize(new SizeInt32((int)Math.Round(MiniViewWidth * scale), (int)Math.Round(MiniViewWidth * 9 / 16 * scale)));
                break;
            default:
                _windowed.Restore(window);
                break;
        }
    }

    // A presenter the window took by itself; the ones set here match the state already.
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPresenterChange)
        {
            return;
        }
        var mode = sender.Presenter.Kind switch
        {
            AppWindowPresenterKind.FullScreen => WindowMode.FullScreen,
            AppWindowPresenterKind.CompactOverlay => WindowMode.MiniView,
            _ => WindowMode.Window,
        };
        if (mode != _modes.Mode)
        {
            _modes.Follow(mode);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
