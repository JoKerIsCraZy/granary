using System.Runtime.InteropServices;
using Granary.Core.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Granary.App.Services;

/// <summary>The desktop's displays as they are now, to place the main window on (<see cref="WindowedPlacement"/>).</summary>
internal static partial class Displays
{
    private const double BaseDpi = 96;
    // MDT_EFFECTIVE_DPI: the scale Windows' display settings give the display.
    private const int EffectiveDpi = 0;

    /// <summary>Every display's work area, the desktop without the taskbar.</summary>
    public static List<WindowBounds> WorkAreas()
    {
        // By index: enumerating FindAll's list has failed in C# (a known fault of its projection).
        var displays = DisplayArea.FindAll();
        var workAreas = new List<WindowBounds>(displays.Count);
        for (int i = 0; i < displays.Count; i++)
        {
            workAreas.Add(Of(displays[i].WorkArea));
        }
        return workAreas;
    }

    /// <summary>The display the bounds lie on most, or the nearest one when they lie on none.</summary>
    public static DisplayArea At(WindowBounds bounds) => DisplayArea.GetFromRect(ToRect(bounds), DisplayAreaFallback.Nearest);

    /// <summary>The display's scale, 1 at 100 %; 1 when Windows does not say.</summary>
    public static double ScaleOf(DisplayArea display)
    {
        nint monitor = Win32Interop.GetMonitorFromDisplayId(display.DisplayId);
        return GetDpiForMonitor(monitor, EffectiveDpi, out uint dpi, out _) == 0 && dpi > 0 ? dpi / BaseDpi : 1;
    }

    public static WindowBounds Of(RectInt32 rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    public static RectInt32 ToRect(WindowBounds bounds) => new(bounds.X, bounds.Y, bounds.Width, bounds.Height);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
