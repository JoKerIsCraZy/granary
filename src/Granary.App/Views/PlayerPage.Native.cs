using System.Runtime.InteropServices;

namespace Granary.App.Views;

// Win32 for what XAML does not offer the player: where the cursor is, and hiding it.
public sealed partial class PlayerPage
{
    private const uint GaRoot = 2;

    [LibraryImport("user32.dll", EntryPoint = "ShowCursor")]
    private static partial int NativeShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(NativePoint point);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(nint window, ref NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
