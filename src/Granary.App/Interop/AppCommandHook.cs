using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Granary.App.Interop;

/// <summary>
/// The window's WM_APPCOMMAND, which some remotes send for a button instead of a key: a Media Center remote's Play and
/// Pause are separate commands, not the Play/Pause key. A command the window does not take goes on to Windows, which
/// hands media commands to the media session.
/// </summary>
internal static unsafe partial class AppCommandHook
{
    /// <summary>APPCOMMAND_MEDIA_PLAY.</summary>
    public const int MediaPlay = 46;

    private const uint WmAppCommand = 0x0319;
    private const int DeviceMask = 0xF000;
    private const nuint SubclassId = 1;

    private static Func<int, bool>? s_take;

    /// <summary>Hooks the window; <paramref name="take"/> says whether the window took a command.</summary>
    public static void Install(nint hwnd, Func<int, bool> take)
    {
        s_take = take;
        if (!SetWindowSubclass(hwnd, &OnMessage, SubclassId, 0))
        {
            throw new InvalidOperationException("The window's commands could not be hooked.");
        }
    }

    // GET_APPCOMMAND_LPARAM: the command in the high word, without the device bits.
    private static int CommandOf(nint lParam) => (int)((lParam >> 16) & 0xFFFF) & ~DeviceMask;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data) =>
        message == WmAppCommand && s_take is { } take && take(CommandOf(lParam))
            ? 1
            : DefSubclassProc(hwnd, message, wParam, lParam);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(
        nint hwnd, delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}
