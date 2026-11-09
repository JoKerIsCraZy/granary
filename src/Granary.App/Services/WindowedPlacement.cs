using Granary.Core.ViewModels;
using Microsoft.UI.Windowing;

namespace Granary.App.Services;

/// <summary>
/// The window as a window: as it was before it went full screen or into the mini view, to go back to
/// (<see cref="WindowModeService"/>), and where it opens at the next start (<see cref="WindowPlace"/>).
/// </summary>
internal sealed class WindowedPlacement
{
    private readonly WindowPlaceKeeper _place = new();
    private AppWindowPresenter? _presenter;

    /// <summary>
    /// Kept to return to: a new overlapped presenter would forget that the window was maximized, and the old one alone
    /// puts a restored window back a little off its place. A maximized window keeps the place it had before, and a
    /// minimized one (-32000) the last one it had (<see cref="WindowPlaceKeeper"/>).
    /// </summary>
    public void Keep(AppWindow window)
    {
        _presenter = window.Presenter;
        _place.Keep(StateOf(window.Presenter), new WindowBounds(window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height));
    }

    /// <summary>Back to the window as it was kept, on a display as the displays are now; <paramref name="scale"/> is the window's.</summary>
    public void Restore(AppWindow window, double scale)
    {
        // Read first: setting the presenter raises the window's Changed event, which can keep the window again on its way.
        var presenter = _presenter;
        var bounds = _place.Bounds;
        if (presenter is not null)
        {
            window.SetPresenter(presenter);
        }
        else
        {
            window.SetPresenter(AppWindowPresenterKind.Overlapped);
        }
        if (window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored } && bounds is { } kept)
        {
            window.MoveAndResize(Displays.ToRect(OnDisplay(kept, scale)));
        }
    }

    /// <summary>Where the window opens at the next start: the place kept, with its display's scale; none before it had one.</summary>
    public WindowPlace? Place => _place.Bounds is { } bounds ? _place.ToPlace(Displays.ScaleOf(Displays.At(bounds))) : null;

    /// <summary>The window, not shown yet, where it was closed, on a display as the displays are now.</summary>
    public void Open(AppWindow window, WindowPlace place)
    {
        var display = Displays.At(place.Bounds);
        var rect = Displays.ToRect(place.OpenOn(Displays.WorkAreas(), Displays.Of(display.WorkArea), Displays.ScaleOf(display)));
        window.MoveAndResize(rect);
        if (window.Size.Width != rect.Width || window.Size.Height != rect.Height)
        {
            // Moved onto a display of another scale, Windows has the window resize for it (WM_DPICHANGED); on that display
            // now, it takes its size again.
            window.MoveAndResize(rect);
        }
        Keep(window);
        if (place.IsMaximized && window.Presenter is OverlappedPresenter overlapped)
        {
            overlapped.Maximize();
            Keep(window);
        }
    }

    // A display unplugged or moved during full screen must not leave the window off-screen; one whose title bar still shows
    // on a display stays as it was, across two displays too.
    private static WindowBounds OnDisplay(WindowBounds bounds, double scale) =>
        bounds.OnDisplays(Displays.WorkAreas(), Displays.Of(Displays.At(bounds).WorkArea), scale);

    private static WindowState StateOf(AppWindowPresenter presenter) => presenter switch
    {
        OverlappedPresenter { State: OverlappedPresenterState.Maximized } => WindowState.Maximized,
        OverlappedPresenter { State: OverlappedPresenterState.Minimized } => WindowState.Minimized,
        _ => WindowState.Restored,
    };
}
