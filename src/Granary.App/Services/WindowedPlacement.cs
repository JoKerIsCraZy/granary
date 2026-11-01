using Granary.Core.ViewModels;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Granary.App.Services;

/// <summary>
/// The window as it was before it went full screen or into the mini view, to go back to (<see cref="WindowModeService"/>).
/// </summary>
internal sealed class WindowedPlacement
{
    // In view pixels: the title bar's height (MainWindow's row), and how much of it has to show on a display for the window
    // to stay where it was (WindowBounds.OnDisplays).
    private const double TitleBarHeight = 32;
    private const double MinimumVisibleTitleBar = 120;

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
            window.MoveAndResize(OnDisplay(kept, scale));
        }
    }

    // A display unplugged or moved during full screen must not leave the window off-screen; one whose title bar still shows
    // on a display stays as it was, across two displays too.
    private static RectInt32 OnDisplay(RectInt32 bounds, double scale)
    {
        // By index: enumerating FindAll's list has failed in C# (a known fault of its projection).
        var displays = DisplayArea.FindAll();
        var workAreas = new List<WindowBounds>(displays.Count);
        for (int i = 0; i < displays.Count; i++)
        {
            workAreas.Add(Of(displays[i].WorkArea));
        }
        var nearest = Of(DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea);
        var placed = Of(bounds).OnDisplays(workAreas, nearest,
            (int)Math.Round(TitleBarHeight * scale), (int)Math.Round(MinimumVisibleTitleBar * scale));
        return new RectInt32(placed.X, placed.Y, placed.Width, placed.Height);
    }

    private static WindowBounds Of(RectInt32 rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
}
