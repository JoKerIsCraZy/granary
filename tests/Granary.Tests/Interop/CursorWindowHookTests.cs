using System.Runtime.InteropServices;
using Granary.App.Interop;

namespace Granary.Tests.Interop;

// A real HWND and WM_SETCURSOR exercise the native boundary without starting WinUI or moving the mouse.
public sealed partial class CursorWindowHookTests
{
    [Fact]
    public void EveryCursorRequest_WhileHidden_UsesTheHiddenCursor()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Requires Windows window messages.");
        }
        nint window = CreateWindowExW(0, "STATIC", "Cursor test", 0, 0, 0, 10, 10, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        nint previous = GetCursor();
        nint blankStandIn = LoadCursorW(0, 32515); // IDC_CROSS: a distinct handle stands in for the blank one.
        bool hidden = true;
        using var hook = new CursorWindowHook(blankStandIn, () => hidden);
        try
        {
            Assert.True(hook.Watch(window));
            foreach (nint shape in new nint[] { 32512, 32649 }) // Arrow and hand.
            {
                SetCursor(LoadCursorW(0, shape));
                Assert.Equal(1, SendMessageW(window, 0x0020, window, (0x0200 << 16) | 1));
                Assert.Equal(blankStandIn, GetCursor());
            }
            // A deliberate move gives WinUI ownership again.
            hidden = false;
            SendMessageW(window, 0x0020, window, (0x0200 << 16) | 1);
            Assert.NotEqual(blankStandIn, GetCursor());
        }
        finally
        {
            hook.Dispose();
            DestroyWindow(window);
            SetCursor(previous);
        }
    }

    [Fact]
    public void NonClientRequests_AndRequestsAfterDetach_AreLeftToWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Requires Windows window messages.");
        }
        nint window = CreateWindowExW(0, "STATIC", "Cursor test", 0, 0, 0, 10, 10, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        nint previous = GetCursor();
        nint blankStandIn = LoadCursorW(0, 32515);
        using var hook = new CursorWindowHook(blankStandIn, () => true);
        try
        {
            Assert.True(hook.Watch(window));
            SendMessageW(window, 0x0020, window, (0x0200 << 16) | 2); // HTCAPTION
            Assert.NotEqual(blankStandIn, GetCursor());
            Assert.True(hook.Watch(0));
            SendMessageW(window, 0x0020, window, (0x0200 << 16) | 1);
            Assert.NotEqual(blankStandIn, GetCursor());
            // Destroying a watched window removes the registration too.
            Assert.True(hook.Watch(window));
            Assert.True(DestroyWindow(window));
            window = 0;
            Assert.True(hook.Watch(0));
        }
        finally
        {
            hook.Dispose();
            if (window != 0)
            {
                DestroyWindow(window);
            }
            SetCursor(previous);
        }
    }

    [Fact]
    public void AWindowInsideTheWatchedOne_AnswersToo_AndSoDoesARequestHandedOnToItsParent()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Requires Windows window messages.");
        }
        nint root = CreateWindowExW(0, "STATIC", "Cursor test", 0, 0, 0, 10, 10, 0, 0, 0, 0);
        nint inner = CreateWindowExW(0, "STATIC", "Cursor test", ChildStyle, 0, 0, 5, 5, root, 0, 0, 0);
        Assert.NotEqual(0, root);
        Assert.NotEqual(0, inner);
        nint previous = GetCursor();
        nint blankStandIn = LoadCursorW(0, 32515);
        using var hook = new CursorWindowHook(blankStandIn, () => true);
        try
        {
            Assert.True(hook.Watch(root));

            // Windows asks the window under the pointer, whichever of them that is.
            SetCursor(LoadCursorW(0, 32512));
            Assert.Equal(1, SendMessageW(inner, 0x0020, inner, (0x0200 << 16) | 1));
            Assert.Equal(blankStandIn, GetCursor());
            // A window that does not answer hands the question to its parent, still naming itself.
            SetCursor(LoadCursorW(0, 32512));
            Assert.Equal(1, SendMessageW(root, 0x0020, inner, (0x0200 << 16) | 1));
            Assert.Equal(blankStandIn, GetCursor());
            Assert.Equal(2, hook.Answered);

            // A window made after the first look is taken in by the next one.
            nint late = CreateWindowExW(0, "STATIC", "Cursor test", ChildStyle, 0, 0, 5, 5, root, 0, 0, 0);
            Assert.True(hook.Watch(root));
            Assert.Equal(1, SendMessageW(late, 0x0020, late, (0x0200 << 16) | 1));
        }
        finally
        {
            hook.Dispose();
            DestroyWindow(root);
            SetCursor(previous);
        }
    }

    [Fact]
    public void AHookThatLetsGo_LeavesTheWindowToTheHookThatCameAfterIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Requires Windows window messages.");
        }
        nint window = CreateWindowExW(0, "STATIC", "Cursor test", 0, 0, 0, 10, 10, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        nint previous = GetCursor();
        nint firstCursor = LoadCursorW(0, 32515);
        nint secondCursor = LoadCursorW(0, 32649);
        var first = new CursorWindowHook(firstCursor, () => true);
        using var second = new CursorWindowHook(secondCursor, () => true);
        try
        {
            // The player page that is left and the one that came after it can both hold the window for a moment.
            Assert.True(first.Watch(window));
            Assert.True(second.Watch(window));
            first.Dispose();

            SetCursor(LoadCursorW(0, 32512));
            Assert.Equal(1, SendMessageW(window, 0x0020, window, (0x0200 << 16) | 1));
            Assert.Equal(secondCursor, GetCursor());
            Assert.Equal(1, second.Answered);

            second.Dispose();
            SetCursor(LoadCursorW(0, 32512));
            SendMessageW(window, 0x0020, window, (0x0200 << 16) | 1);
            Assert.NotEqual(secondCursor, GetCursor());
        }
        finally
        {
            first.Dispose();
            DestroyWindow(window);
            SetCursor(previous);
        }
    }

    // WS_CHILD.
    private const uint ChildStyle = 0x40000000;

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nint LoadCursorW(nint instance, nint name);

    [LibraryImport("user32.dll")]
    private static partial nint SetCursor(nint cursor);

    [LibraryImport("user32.dll")]
    private static partial nint GetCursor();
}
