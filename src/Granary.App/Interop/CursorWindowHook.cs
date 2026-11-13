using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Granary.App.Interop;

/// <summary>
/// Answers cursor requests on the input HWND before WinUI can restore a visible shape. Scoped to the player over its
/// picture; the title bar, menus and other windows keep their cursors. Install, move and dispose on the window's thread.
/// </summary>
internal sealed unsafe partial class CursorWindowHook(nint hiddenCursor, Func<bool> shouldHide) : IDisposable
{
    private const uint WmSetCursor = 0x0020;
    private const uint WmNcDestroy = 0x0082;
    private const nuint SubclassId = 2; // WindowMessageHook uses 1.

    // Native callbacks must not keep a page alive after it leaves the visual tree.
    private static readonly ConcurrentDictionary<nint, WeakReference<CursorWindowHook>> Hooks = new();
    private nint _window;

    /// <summary>How many cursor requests the hook has answered with the hidden cursor; the log names it.</summary>
    public int Answered { get; private set; }

    /// <summary>Follows the input window under the pointer, or detaches for 0; false if Windows refused the hook.</summary>
    public bool Watch(nint window)
    {
        if (window == _window)
        {
            return true;
        }
        Dispose();
        if (window == 0)
        {
            return true;
        }
        if (!SetWindowSubclass(window, &OnMessage, SubclassId, 0))
        {
            return false;
        }
        _window = window;
        Hooks[window] = new(this);
        return true;
    }

    public void Dispose()
    {
        if (_window == 0)
        {
            return;
        }
        RemoveWindowSubclass(_window, &OnMessage, SubclassId);
        Hooks.TryRemove(_window, out _);
        _window = 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint OnMessage(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (!Hooks.TryGetValue(window, out var weak) || !weak.TryGetTarget(out var hook))
        {
            RemoveWindowSubclass(window, &OnMessage, SubclassId);
            Hooks.TryRemove(window, out _);
            return DefSubclassProc(window, message, wParam, lParam);
        }
        try
        {
            if (message == WmNcDestroy)
            {
                hook.Dispose();
            }
            // HTCLIENT, with a mouse message: do not interfere with non-client resizing or Windows' menu mode.
            else if (message == WmSetCursor && wParam == window && (lParam & 0xFFFF) == 1
                && ((lParam >> 16) & 0xFFFF) != 0 && hook.HiddenCursor() is var cursor && cursor != 0)
            {
                SetCursor(cursor);
                hook.Answered++;
                return 1;
            }
        }
        catch (Exception ex)
        {
            // No managed exception may cross the native window procedure.
            System.Diagnostics.Debug.WriteLine($"The player's cursor request failed: {ex.GetType().Name}");
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private nint HiddenCursor() => shouldHide() ? hiddenCursor : 0;

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint window,
        delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint window,
        delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nint SetCursor(nint cursor);
}
