using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Granary.App.Interop;

/// <summary>
/// The main window's messages that WinUI does not hand on, through one comctl32 subclass made with the window, before it
/// first shows:
/// <list type="bullet">
/// <item>WM_APPCOMMAND, which some remotes send for a button instead of a key: a Media Center remote's Play and Pause are
/// separate commands, not the Play/Pause key. A command the window does not take goes on to Windows, which hands media
/// commands to the media session.</item>
/// <item>The taskbar's, for the Play/Pause in its preview (<c>WindowsTaskbarPlayButton</c>): Explorer's
/// TaskbarButtonCreated, a thumbnail toolbar button's click, and the window's DPI changing.</item>
/// </list>
/// The events are raised inside the window's message, on the UI thread: nothing may escape a handler.
/// </summary>
internal static unsafe partial class WindowMessageHook
{
    /// <summary>APPCOMMAND_MEDIA_PLAY.</summary>
    public const int MediaPlay = 46;

    private const uint WmCommand = 0x0111;
    private const uint WmAppCommand = 0x0319;
    private const uint WmDpiChanged = 0x02E0;
    private const int DeviceMask = 0xF000;
    // THBN_CLICKED, in WM_COMMAND's high word of wParam; the low word is the button's id.
    private const int ThumbButtonClick = 0x1800;
    private const uint MessageFilterAllow = 1;
    private const nuint SubclassId = 1;

    private static Func<int, bool>? s_take;

    // Registered by name, the same number for every app in the session; 0 if that failed.
    private static uint s_taskbarButtonCreated;

    /// <summary>Explorer made the window's taskbar button: the first time the window showed, or again after it restarted.</summary>
    public static event Action? TaskbarButtonCreated;

    /// <summary>A button of the window's thumbnail toolbar was pressed, by its id.</summary>
    public static event Action<int>? ThumbButtonClicked;

    /// <summary>The window moved to a display of another scale, or the scale changed.</summary>
    public static event Action? DpiChanged;

    /// <summary>The window has a taskbar button, so its preview can take buttons.</summary>
    public static bool HasTaskbarButton { get; private set; }

    /// <summary>Hooks the window; <paramref name="take"/> says whether the window took a command.</summary>
    public static void Install(nint hwnd, Func<int, bool> take)
    {
        s_take = take;
        s_taskbarButtonCreated = RegisterWindowMessageW("TaskbarButtonCreated");
        // Run as administrator, Windows would keep Explorer's messages from the window (it runs with fewer rights): its
        // TaskbarButtonCreated, and the WM_COMMAND a preview button's press comes as. Without administrator rights this
        // changes nothing.
        if (s_taskbarButtonCreated != 0)
        {
            ChangeWindowMessageFilterEx(hwnd, s_taskbarButtonCreated, MessageFilterAllow, 0);
        }
        ChangeWindowMessageFilterEx(hwnd, WmCommand, MessageFilterAllow, 0);
        if (!SetWindowSubclass(hwnd, &OnMessage, SubclassId, 0))
        {
            throw new InvalidOperationException("The window's commands could not be hooked.");
        }
    }

    // GET_APPCOMMAND_LPARAM: the command in the high word, without the device bits.
    private static int CommandOf(nint lParam) => (int)((lParam >> 16) & 0xFFFF) & ~DeviceMask;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        try
        {
            if (message == WmAppCommand && s_take is { } take && take(CommandOf(lParam)))
            {
                return 1;
            }
            if (message == WmCommand && ((wParam >> 16) & 0xFFFF) == ThumbButtonClick && ThumbButtonClicked is { } clicked)
            {
                clicked((int)(wParam & 0xFFFF));
                return 0;
            }
            if (message == s_taskbarButtonCreated && s_taskbarButtonCreated != 0)
            {
                HasTaskbarButton = true;
                TaskbarButtonCreated?.Invoke();
            }
            else if (message == WmDpiChanged)
            {
                DpiChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            // The handlers log their own failures; an exception leaving this method would end the process.
            System.Diagnostics.Debug.WriteLine($"A window message could not be handled: {ex.GetType().Name}");
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(
        nint hwnd, delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint changeInfo);
}
