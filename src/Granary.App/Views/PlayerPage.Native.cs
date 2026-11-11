using System.Runtime.InteropServices;

namespace Granary.App.Views;

// Win32 for what XAML does not offer the player: where the cursor is, and having it drawn again.
public sealed partial class PlayerPage
{
    private const uint GaRoot = 2;

    private const uint WmSetCursor = 0x0020;

    // WM_SETCURSOR's lParam as a mouse move over the client area sends it: HTCLIENT (1), WM_MOUSEMOVE (0x0200).
    private const nint CursorRequestForMove = (0x0200 << 16) | 1;

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

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
