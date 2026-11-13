using System.Runtime.InteropServices;

namespace Granary.App.Views;

// Win32 for what XAML does not offer the player: where the pointer is, and which cursor Windows draws.
public sealed partial class PlayerPage
{
    private const uint GaRoot = 2;

    // CURSOR_SHOWING.
    private const int CursorShowing = 0x00000001;

    // Windows' own cursors as LoadCursor takes them (IDC_ARROW to IDC_HELP), and their handles once asked for.
    private static readonly nint[] SystemCursorIds =
        [32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651];

    private static nint[]? s_systemCursors;

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

    [LibraryImport("user32.dll")]
    private static partial nint LoadCursorW(nint instance, nint name);

    // For the log's account of a cursor over the shield: which window was under the pointer, and which one is in front.
    [LibraryImport("user32.dll")]
    private static unsafe partial int GetClassNameW(nint window, char* name, int capacity);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorInfo(ref CursorInfo info);

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
