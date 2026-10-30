using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Granary.App.Services;

/// <summary>
/// The window as it was before it went full screen or into the mini view, to go back to: the player's (PlayerPage) and a
/// trailer's (TrailerOverlay).
/// </summary>
internal sealed class WindowedPlacement
{
    private AppWindowPresenter? _presenter;
    private RectInt32? _bounds;

    /// <summary>
    /// Kept to return to: a new overlapped presenter would forget that the window was maximized, and the old one alone
    /// puts a restored window back a little off its place.
    /// </summary>
    public void Keep(AppWindow window)
    {
        _presenter = window.Presenter;
        _bounds = new RectInt32(window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height);
    }

    public void Restore(AppWindow window)
    {
        if (_presenter is { } presenter)
        {
            window.SetPresenter(presenter);
        }
        else
        {
            window.SetPresenter(AppWindowPresenterKind.Overlapped);
        }
        if (window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored } && _bounds is { } bounds)
        {
            window.MoveAndResize(bounds);
        }
    }
}
