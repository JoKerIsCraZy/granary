using Granary.Core.ViewModels;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Granary.App.Services;

/// <summary>
/// The window as it was before it went full screen or into the mini view, to go back to (<see cref="WindowModeService"/>).
/// </summary>
internal sealed class WindowedPlacement
{
    // How far a window's invisible resize frame may reach past a display's edge, in view pixels: a window flush with the
    // edge comes back where it was, not nudged inwards.
    private const double FrameSlack = 16;

    private AppWindowPresenter? _presenter;
    private RectInt32? _bounds;

    /// <summary>
    /// Kept to return to: a new overlapped presenter would forget that the window was maximized, and the old one alone
    /// puts a restored window back a little off its place. A minimized window has no place of its own (-32000): the last
    /// one it had is kept.
    /// </summary>
    public void Keep(AppWindow window)
    {
        _presenter = window.Presenter;
        if (window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
        {
            return;
        }
        _bounds = new RectInt32(window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height);
    }

    /// <summary>Back to the window as it was kept, on a display as the displays are now; <paramref name="scale"/> is the window's.</summary>
    public void Restore(AppWindow window, double scale)
    {
        // Read first: setting the presenter raises the window's Changed event, which can keep the window again on its way.
        var presenter = _presenter;
        var bounds = _bounds;
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
            window.MoveAndResize(OnDisplay(kept, (int)Math.Ceiling(FrameSlack * scale)));
        }
    }

    // A display unplugged or moved during full screen must not leave the window off-screen: the nearest one's work area.
    private static RectInt32 OnDisplay(RectInt32 bounds, int slack)
    {
        var area = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
        var fitted = new WindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height)
            .Within(new WindowBounds(area.X, area.Y, area.Width, area.Height), slack);
        return new RectInt32(fitted.X, fitted.Y, fitted.Width, fitted.Height);
    }
}
