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

    // CURSOR_SHOWING, and IDC_ARROW as LoadCursor takes it.
    private const int CursorShowing = 0x00000001;
    private const nint ArrowCursorId = 32512;

    [LibraryImport("user32.dll")]
    private static partial nint LoadCursorW(nint instance, nint name);

    // For the log's account of a cursor that would not hide: which window, whose, and which one is in front.
    [LibraryImport("user32.dll")]
    private static unsafe partial int GetClassNameW(nint window, char* name, int capacity);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint process);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorInfo(ref CursorInfo info);

    // Sets the cursor Windows draws for this thread's windows, until one of them is asked for its cursor again.
    [LibraryImport("user32.dll")]
    private static partial nint SetCursor(nint cursor);

    // CURSORINFO: the cursor Windows draws now.
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public nint Cursor;
        public NativePoint Position;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
