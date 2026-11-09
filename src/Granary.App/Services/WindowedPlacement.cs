using System.Runtime.InteropServices;
using Granary.Core.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Windowing;

namespace Granary.App.Services;

/// <summary>
/// The window as a window: as it was before it went full screen or into the mini view, to go back to
/// (<see cref="WindowModeService"/>), and where it opens at the next start (<see cref="WindowPlace"/>).
/// </summary>
internal sealed partial class WindowedPlacement(ILogger logger)
{
    private readonly WindowPlaceKeeper _place = new();
    private AppWindowPresenter? _presenter;
    private bool _isScaleFailureLogged;

    /// <summary>
    /// Kept to return to: a new overlapped presenter would forget that the window was maximized, and the old one alone
    /// puts a restored window back a little off its place. A maximized window keeps the place Windows restores it to, and
    /// a minimized one (-32000) the last one it had (<see cref="WindowPlaceKeeper"/>).
    /// </summary>
    public void Keep(AppWindow window)
    {
        _presenter = window.Presenter;
        var state = StateOf(window.Presenter);
        var bounds = new WindowBounds(window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height);
        _place.Keep(state, bounds, state == WindowState.Maximized ? RestoreBounds(window) : null);
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
    public WindowPlace? Place => _place.Bounds is { } bounds ? _place.ToPlace(ScaleOf(Displays.At(bounds))) : null;

    /// <summary>
    /// The window, not shown yet, where it was closed, on a display as the displays are now; restored, so as not to show
    /// it yet (<see cref="Maximize"/>).
    /// </summary>
    public void Open(AppWindow window, WindowPlace place)
    {
        var display = Displays.At(place.Bounds);
        var rect = Displays.ToRect(place.OpenOn(Displays.WorkAreas(), Displays.Of(display.WorkArea), ScaleOf(display)));
        window.MoveAndResize(rect);
        if (window.Size.Width != rect.Width || window.Size.Height != rect.Height)
        {
            // Moved onto a display of another scale, Windows has the window resize for it (WM_DPICHANGED); on that display
            // now, it takes its size again.
            window.MoveAndResize(rect);
        }
        Keep(window);
    }

    /// <summary>Maximizes the window, which shows it, and keeps it so; un-maximizing goes back to its place.</summary>
    public void Maximize(AppWindow window)
    {
        if (window.Presenter is OverlappedPresenter overlapped)
        {
            overlapped.Maximize();
            Keep(window);
        }
    }

    // A display unplugged or moved during full screen must not leave the window off-screen; one whose title bar still shows
    // on a display stays as it was, across two displays too.
    private static WindowBounds OnDisplay(WindowBounds bounds, double scale) =>
        bounds.OnDisplays(Displays.WorkAreas(), Displays.Of(Displays.At(bounds).WorkArea), scale);

    private double ScaleOf(DisplayArea display)
    {
        if (!Displays.TryScaleOf(display, out double scale, out int result) && !_isScaleFailureLogged)
        {
            _isScaleFailureLogged = true;
            logger.LogDebug("Windows did not say a display's scale (0x{Result:X8}); the window's place counts it as 100 %", result);
        }
        return scale;
    }

    // Where Windows restores the maximized window to. It follows the window to another display (Win+Shift+Arrow), which the
    // window's own bounds while restored do not see. Windows gives it in workspace coordinates, off by the app bars of the
    // window's display; none when it does not say.
    private static WindowBounds? RestoreBounds(AppWindow window)
    {
        var placement = new NativePlacement { Length = (uint)Marshal.SizeOf<NativePlacement>() };
        if (!GetWindowPlacement(Win32Interop.GetWindowFromWindowId(window.Id), ref placement))
        {
            return null;
        }
        var normal = placement.NormalPosition;
        var display = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest);
        return new WindowBounds(normal.Left, normal.Top, normal.Right - normal.Left, normal.Bottom - normal.Top)
            .FromWorkspace(Displays.Of(display.OuterBounds), Displays.Of(display.WorkArea));
    }

    private static WindowState StateOf(AppWindowPresenter presenter) => presenter switch
    {
        OverlappedPresenter { State: OverlappedPresenterState.Maximized } => WindowState.Maximized,
        OverlappedPresenter { State: OverlappedPresenterState.Minimized } => WindowState.Minimized,
        _ => WindowState.Restored,
    };

    // WINDOWPLACEMENT.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePlacement
    {
        public uint Length;
        public uint Flags;
        public uint ShowCommand;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(nint window, ref NativePlacement placement);
}
