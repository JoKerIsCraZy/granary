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
    // WINDOWPLACEMENT's WPF_RESTORETOMAXIMIZED, and SW_SHOWMINIMIZED.
    private const uint RestoreToMaximizedFlag = 0x2;
    private const uint ShowMinimized = 2;

    private AppWindowPresenter? _presenter;
    // Opened in full screen, closed so while maximized: maximized as it comes back from full screen (MaximizeOnRestore).
    private bool _maximizeOnRestore;
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
        if (state == WindowState.Maximized)
        {
            // Checked against the display the maximized window is on.
            var display = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest);
            _place.KeepMaximized(RestoreBounds(window), Displays.Of(display.WorkArea), ScaleOf(display));
            return;
        }
        _place.Keep(state, new WindowBounds(window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height));
    }

    /// <summary>Back to the window as it was kept, on a display as the displays are now; <paramref name="scale"/> is the window's.</summary>
    public void Restore(AppWindow window, double scale)
    {
        // Read first: setting the presenter raises the window's Changed event, which can keep the window again on its way.
        var presenter = _presenter;
        var bounds = _place.Bounds;
        bool maximize = _maximizeOnRestore;
        _maximizeOnRestore = false;
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
        if (maximize)
        {
            Maximize(window);
        }
    }

    /// <summary>
    /// The window opens in full screen, closed so while it was maximized: maximizing it first would show it as a window, so
    /// it is maximized as it leaves full screen instead, and counts as maximized meanwhile, also when it closes so again.
    /// </summary>
    public void MaximizeOnRestore()
    {
        _maximizeOnRestore = true;
        _place.Keep(WindowState.Maximized, default);
    }

    /// <summary>
    /// Where the window opens at the next start: the place kept, with its display's scale, and in full screen when
    /// <paramref name="isFullScreen"/>; none before it had one.
    /// </summary>
    public WindowPlace? PlaceFor(bool isFullScreen)
    {
        if (_place.Bounds is not { } bounds || _place.ToPlace(ScaleOf(Displays.At(bounds))) is not { } place)
        {
            return null;
        }
        return place with { IsFullScreen = isFullScreen };
    }

    /// <summary>The window is minimized, whichever presenter it has; a window that no longer says counts as not.</summary>
    public static bool IsMinimized(AppWindow window) => IsIconic(Win32Interop.GetWindowFromWindowId(window.Id));

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

    /// <summary>
    /// A window that should be maximized but shows minimized (a shortcut's "Run: Minimized" took its first show): it comes
    /// back maximized from the taskbar, and counts as maximized meanwhile, also when it closes from there. When Windows does
    /// not take that, it stays as it is: restored from the taskbar, it is kept as a restored window.
    /// </summary>
    public void RestoreToMaximized(AppWindow window)
    {
        var placement = new NativePlacement { Length = (uint)Marshal.SizeOf<NativePlacement>() };
        nint handle = Win32Interop.GetWindowFromWindowId(window.Id);
        if (!GetWindowPlacement(handle, ref placement))
        {
            logger.LogDebug("Windows did not say the minimized window's placement (error {Error}); it comes back as Windows restores it",
                Marshal.GetLastPInvokeError());
            return;
        }
        // Windows takes the flag only with the minimized show command, and only for the next restore.
        placement.Flags |= RestoreToMaximizedFlag;
        placement.ShowCommand = ShowMinimized;
        if (!SetWindowPlacement(handle, ref placement))
        {
            logger.LogDebug("Windows did not take the minimized window's restore to maximized (error {Error})",
                Marshal.GetLastPInvokeError());
            return;
        }
        _place.Keep(WindowState.Maximized, default);
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
    // window's own bounds while restored do not see. Windows gives it in workspace coordinates, off by the app bars of a
    // display: here those of the display the rect itself lies on (DisplayArea.GetFromRect, MonitorFromRect's nearest), as
    // Chromium turns them. A taskbar at the bottom, as on most PCs, makes no difference; should that be the wrong display,
    // WindowPlaceKeeper.KeepMaximized does not trust a rect off the window's own. None when Windows does not say.
    private static WindowBounds? RestoreBounds(AppWindow window)
    {
        var placement = new NativePlacement { Length = (uint)Marshal.SizeOf<NativePlacement>() };
        if (!GetWindowPlacement(Win32Interop.GetWindowFromWindowId(window.Id), ref placement))
        {
            return null;
        }
        var normal = placement.NormalPosition;
        var workspace = new WindowBounds(normal.Left, normal.Top, normal.Right - normal.Left, normal.Bottom - normal.Top);
        if (workspace.Width <= 0 || workspace.Height <= 0)
        {
            return null;
        }
        var display = Displays.At(workspace);
        return workspace.FromWorkspace(Displays.Of(display.OuterBounds), Displays.Of(display.WorkArea));
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

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(nint window, ref NativePlacement placement);

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPlacement(nint window, ref NativePlacement placement);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);
}
