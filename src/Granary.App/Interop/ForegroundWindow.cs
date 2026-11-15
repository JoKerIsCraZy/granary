using System.Runtime.InteropServices;

namespace Granary.App.Interop;

/// <summary>Whether a window is the one in front: Windows' foreground window, and not minimized.</summary>
internal static partial class ForegroundWindow
{
    public static bool Is(nint hwnd) => hwnd != 0 && GetForegroundWindow() == hwnd && !IsIconic(hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hwnd);
}
