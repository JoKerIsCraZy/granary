using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Granary.App.Interop;

/// <summary>
/// Answers Windows' question for the cursor (WM_SETCURSOR) with a hidden one, on a window and every window in it, before
/// their own procedures do: the one place Windows documents as reliable for setting a cursor, since one set at any other
/// time holds only while the thread has the mouse's input. Every window is hooked rather than the one under the pointer,
/// so it does not matter which of them Windows asks, nor whether one hands the question on to its parent.
/// <paramref name="shouldHide"/> says for each request whether the cursor is to be hidden now; without it the windows
/// answer as they always did, so the title bar, menus and other windows keep their cursors. Use it on the windows' thread.
/// </summary>
internal sealed unsafe partial class CursorWindowHook : IDisposable
{
    private const uint WmSetCursor = 0x0020;
    private const uint WmNcDestroy = 0x0082;
    private const int HitClient = 1;
    private const nuint SubclassId = 2; // WindowMessageHook uses 1.

    // Which hook answers for a window. Weak: a native callback must not keep a page alive after it left the visual tree.
    private static readonly ConcurrentDictionary<nint, WeakReference<CursorWindowHook>> Hooks = new();

    [ThreadStatic]
    private static List<nint>? t_found;

    private readonly HashSet<nint> _windows = [];
    private readonly WeakReference<CursorWindowHook> _self;
    private readonly nint _hiddenCursor;
    private readonly Func<bool> _shouldHide;

    public CursorWindowHook(nint hiddenCursor, Func<bool> shouldHide)
    {
        _hiddenCursor = hiddenCursor;
        _shouldHide = shouldHide;
        _self = new(this);
    }

    /// <summary>How many requests the hook has answered with the hidden cursor; the log names it.</summary>
    public int Answered { get; private set; }

    /// <summary>
    /// Hooks <paramref name="root"/> and every window in it that is this thread's, also the ones made since the last call;
    /// 0 lets go of all. False when Windows refused the hook on the root.
    /// </summary>
    public bool Watch(nint root)
    {
        if (root == 0)
        {
            Release();
            return true;
        }
        uint thread = GetCurrentThreadId();
        foreach (nint window in Tree(root))
        {
            // Another hook may have taken a window over since (a page that came after this one): it is taken back while
            // this one is still asked to watch. comctl32 refuses a window of another thread.
            if ((_windows.Contains(window) && Owns(window)) || GetWindowThreadProcessId(window, out _) != thread)
            {
                continue;
            }
            if (SetWindowSubclass(window, &OnMessage, SubclassId, 0))
            {
                Hooks[window] = _self;
                _windows.Add(window);
            }
        }
        return _windows.Contains(root);
    }

    public void Dispose() => Release();

    // Only the windows this hook still answers for: one another hook took over stays that hook's.
    private void Release()
    {
        foreach (nint window in _windows)
        {
            if (Owns(window))
            {
                RemoveWindowSubclass(window, &OnMessage, SubclassId);
                Hooks.TryRemove(new KeyValuePair<nint, WeakReference<CursorWindowHook>>(window, _self));
            }
        }
        _windows.Clear();
    }

    private bool Owns(nint window) => Hooks.TryGetValue(window, out var weak) && ReferenceEquals(weak, _self);

    // The window and all windows in it, at every depth.
    private static List<nint> Tree(nint root)
    {
        var found = t_found ??= [];
        found.Clear();
        found.Add(root);
        EnumChildWindows(root, &OnChild, 0);
        return [.. found];
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnChild(nint window, nint state)
    {
        t_found?.Add(window);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint OnMessage(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        try
        {
            if (!Hooks.TryGetValue(window, out var weak) || !weak.TryGetTarget(out var hook))
            {
                // The hook is gone without having let go: the window answers for itself again.
                RemoveWindowSubclass(window, &OnMessage, SubclassId);
                if (weak is not null)
                {
                    Hooks.TryRemove(new KeyValuePair<nint, WeakReference<CursorWindowHook>>(window, weak));
                }
            }
            else if (message == WmNcDestroy)
            {
                RemoveWindowSubclass(window, &OnMessage, SubclassId);
                Hooks.TryRemove(new KeyValuePair<nint, WeakReference<CursorWindowHook>>(window, weak));
                hook._windows.Remove(window);
            }
            // The client area only, and with the mouse message that asked (the high word): it is 0 while the window is in
            // menu mode, and off the client area Windows sets the cursor itself (sizing borders, the title bar). Whether
            // the question is this window's own or one a window in it handed on does not matter.
            else if (message == WmSetCursor && (lParam & 0xFFFF) == HitClient && ((lParam >> 16) & 0xFFFF) != 0
                && hook._shouldHide())
            {
                SetCursor(hook._hiddenCursor);
                hook.Answered++;
                return 1;
            }
        }
        catch (Exception ex)
        {
            // No managed exception may cross the native window procedure; the window then answers for itself.
            System.Diagnostics.Debug.WriteLine($"The player's cursor request failed: {ex.GetType().Name}");
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumChildWindows(nint parent, delegate* unmanaged[Stdcall]<nint, nint, int> proc, nint state);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint process);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    private static partial nint SetCursor(nint cursor);
}
