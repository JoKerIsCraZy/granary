using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Granary.App.Interop;

/// <summary>What was on a <see cref="CursorShield"/>: Windows says it with every mouse message it makes.</summary>
internal enum ShieldPointer
{
    Mouse,
    Touch,
    Pen,
}

/// <summary>What a <see cref="CursorShield"/> tells of the pointer on it, inside the window's message: nothing may escape.</summary>
internal interface ICursorShieldOwner
{
    /// <summary>
    /// A move of the mouse or of a pen reached the shield: a real one, or one Windows makes up whenever a window changes.
    /// The owner tells them apart, by where the pointer is. Not the move Windows makes to where a finger goes down.
    /// </summary>
    void MouseSeen();

    /// <summary>The left button, a finger or a pen went down on the shield.</summary>
    void Pressed(ShieldPointer pointer);

    /// <summary>
    /// It came up again close to where it went down: a click or a tap. <paramref name="isSecond"/> for the second one of a
    /// double click, which follows a first one said without it.
    /// </summary>
    void Clicked(ShieldPointer pointer, bool isSecond);

    /// <summary>The mouse's back button went down on the shield.</summary>
    void BackPressed();

    /// <summary>
    /// Another button went down on the shield (the right one, the middle one, the mouse's forward button): the pointer is
    /// in use, but nothing is clicked.
    /// </summary>
    void Woken(ShieldPointer pointer);
}

/// <summary>
/// An invisible window of Granary's own that lies over the player's picture while the cursor is hidden, so that Windows
/// asks Granary for the cursor there and not WinUI.
/// <para>
/// Over WinUI content the cursor is WinUI's to set, and WinUI loses it: whenever Windows works the cursor out again
/// without the mouse having moved, as it does when any window changes, WinUI shows the arrow, and nothing an app sets
/// (an element's cursor, the window's, SetCursor) shows until the mouse really moves (microsoft-ui-xaml issue 11942). A
/// hidden cursor came back that way, by itself. Over a plain window none of that applies: Windows asks it with
/// WM_SETCURSOR each time, and the answer is drawn. WinUI does the same for the title bar, with a window made like this
/// one: a child of the main window, layered and without a surface of its own, so nothing of it is drawn, it takes
/// nothing from what the windows under it show, and what lies under it shows.
/// </para>
/// <para>
/// Windows finds such a window under the pointer only where it was when it was made: one made empty and sized later is
/// passed over (measured; one with a surface, or with layered attributes, is not). So the shield's window is made at
/// the place it is to cover, and made anew when that place changes, which it rarely does.
/// </para>
/// <para>
/// The price is that the pointer is no longer WinUI's while the shield is up, so the shield hands on what happens on
/// it (<see cref="ICursorShieldOwner"/>). Use it on the window's thread, and dispose it there.
/// </para>
/// </summary>
internal sealed unsafe partial class CursorShield : IDisposable
{
    private const uint WmEraseBackground = 0x0014;
    private const uint WmSetCursor = 0x0020;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLeftDown = 0x0201;
    private const uint WmLeftUp = 0x0202;
    private const uint WmLeftDouble = 0x0203;
    private const uint WmRightDown = 0x0204;
    private const uint WmRightUp = 0x0205;
    private const uint WmMiddleDown = 0x0207;
    private const uint WmMiddleUp = 0x0208;
    private const uint WmXDown = 0x020B;
    private const uint WmXUp = 0x020C;
    private const uint WmCaptureChanged = 0x0215;

    private const int HitClient = 1;
    private const int BackButton = 1; // XBUTTON1.

    // CS_DBLCLKS: Windows says the second press of a double click as one.
    private const uint ClassDoubleClicks = 0x0008;
    private const uint StyleChild = 0x40000000;

    // WS_EX_LAYERED and WS_EX_NOREDIRECTIONBITMAP, as WinUI's own input window over the title bar has them.
    private const uint ExLayered = 0x00080000;
    private const uint ExNoSurface = 0x00200000;

    // SetWindowPos: HWND_TOP, SWP_NOSIZE, SWP_NOMOVE, SWP_NOACTIVATE and SWP_SHOWWINDOW; ShowWindow: SW_HIDE.
    private const nint OverTheOthers = 0;
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoActivate = 0x0010;
    private const uint Show = 0x0040;
    private const int Hide = 0;

    // The signature of a mouse message Windows made from a finger or a pen, and the bit that says a finger.
    private const uint FromTouchOrPen = 0xFF515700;
    private const uint SignatureMask = 0xFFFFFF00;
    private const uint FingerBit = 0x80;

    // How far, in view pixels, a release may be from its press and still be a click: a hand that clicks moves the mouse.
    private const int ClickReach = 16;
    private const int DefaultDpi = 96;

    // Which shield a window is. Weak: a native callback must not keep a page alive after it left the visual tree.
    private static readonly ConcurrentDictionary<nint, WeakReference<CursorShield>> Shields = new();

    private static readonly Lazy<ushort> WindowClass = new(Register);

    private readonly WeakReference<CursorShield> _self;
    private readonly nint _parent;
    private readonly nint _hiddenCursor;
    private readonly ICursorShieldOwner _owner;
    private (int X, int Y, int Width, int Height) _place;
    private Press? _press;

    // The picture was uncovered while a button was down on the shield: it goes with the release.
    private bool _leavesWithRelease;
    private bool _isLettingGo;
    private bool _isDisposed;

    /// <summary>
    /// A shield for <paramref name="parent"/>'s picture, not laid yet. <paramref name="hiddenCursor"/> is the cursor it
    /// answers Windows with.
    /// </summary>
    public CursorShield(nint parent, nint hiddenCursor, ICursorShieldOwner owner)
    {
        _parent = parent;
        _hiddenCursor = hiddenCursor;
        _owner = owner;
        _self = new(this);
    }

    /// <summary>The shield's window, 0 before it is first laid, where it could not be made, and once it is gone.</summary>
    public nint Window { get; private set; }

    /// <summary>Whether the shield lies over the picture now.</summary>
    public bool IsUp { get; private set; }

    /// <summary>
    /// Lays the shield over a place of its parent, in the parent's client pixels, over the parent's other windows and
    /// without taking the foreground. Windows then asks it for the cursor at once. The same place again changes nothing,
    /// since every change of a window has Windows work the cursor out again, and another place during a press waits for
    /// the next call after the release. False when nothing is covered: no place, or Windows made no window.
    /// </summary>
    public bool Cover(int x, int y, int width, int height)
    {
        if (_isDisposed || width <= 0 || height <= 0)
        {
            Uncover();
            return false;
        }
        _leavesWithRelease = false;
        var place = (x, y, width, height);
        if (Window != 0 && place == _place)
        {
            if (!IsUp)
            {
                IsUp = SetWindowPos(Window, OverTheOthers, 0, 0, 0, 0, NoSize | NoMove | NoActivate | Show);
            }
            return IsUp;
        }
        if (_press is not null && Window != 0)
        {
            // Elsewhere, during a press: the window stays for the release, or the click would be lost. The owner lays the
            // shield again at its next look.
            return IsUp;
        }
        // Elsewhere, or not made yet: made at its place.
        Unmake();
        _place = place;
        Make();
        IsUp = Window != 0 && SetWindowPos(Window, OverTheOthers, 0, 0, 0, 0, NoSize | NoMove | NoActivate | Show);
        return IsUp;
    }

    /// <summary>
    /// Takes the shield away: the pointer is WinUI's again. While a button is down on it, it stays for the release, or the
    /// click would be lost between the two.
    /// </summary>
    public void Uncover()
    {
        if (!IsUp)
        {
            return;
        }
        if (_press is not null)
        {
            _leavesWithRelease = true;
            return;
        }
        Leave();
    }

    public void Dispose()
    {
        _isDisposed = true;
        Unmake();
    }

    private void Make()
    {
        ushort windowClass = WindowClass.Value;
        if (windowClass == 0)
        {
            return;
        }
        nint module = GetModuleHandleW(0);
        var (x, y, width, height) = _place;
        Window = CreateWindowExW(ExLayered | ExNoSurface, windowClass, 0, StyleChild, x, y, width, height, _parent, 0, module, 0);
        if (Window == 0)
        {
            // Windows makes a layered child only for a program that says it knows Windows 8, as Granary does.
            Window = CreateWindowExW(ExNoSurface, windowClass, 0, StyleChild, x, y, width, height, _parent, 0, module, 0);
        }
        if (Window != 0)
        {
            Shields[Window] = _self;
        }
    }

    private void Unmake()
    {
        nint window = Window;
        if (window == 0)
        {
            return;
        }
        Forget(window);
        _ = DestroyWindow(window);
    }

    /// <summary>What made a mouse message, from what Windows notes with it (GetMessageExtraInfo).</summary>
    internal static ShieldPointer PointerOf(nint extraInfo)
    {
        uint info = unchecked((uint)extraInfo);
        if ((info & SignatureMask) != FromTouchOrPen)
        {
            return ShieldPointer.Mouse;
        }
        return (info & FingerBit) != 0 ? ShieldPointer.Touch : ShieldPointer.Pen;
    }

    private void Leave()
    {
        IsUp = false;
        _leavesWithRelease = false;
        _ = ShowWindow(Window, Hide);
    }

    private void Forget(nint window)
    {
        Shields.TryRemove(new KeyValuePair<nint, WeakReference<CursorShield>>(window, _self));
        Window = 0;
        IsUp = false;
        _press = null;
        _leavesWithRelease = false;
    }

    // The answer to a message, or null for Windows' own.
    private nint? Answer(nint window, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmSetCursor:
                _ = SetCursor(_hiddenCursor);
                return 1;
            case WmNcHitTest:
                return HitClient;
            case WmEraseBackground:
                return 1;
            case WmMouseMove:
                // A finger's is none: Windows moves its pointer to where the finger goes down, before the press.
                if (PointerOf(GetMessageExtraInfo()) != ShieldPointer.Touch)
                {
                    _owner.MouseSeen();
                }
                return 0;
            case WmLeftDown:
            case WmLeftDouble:
                Pressed(isSecond: message == WmLeftDouble, lParam);
                return 0;
            case WmLeftUp:
                Released(wParam, lParam);
                return 0;
            case WmCaptureChanged:
                if (!_isLettingGo)
                {
                    // Another window took the mouse while the button was down: no click comes of this press.
                    EndPress();
                }
                return 0;
            case WmXDown when ((wParam >> 16) & 0xFFFF) == BackButton:
                _owner.BackPressed();
                return 1;
            case WmRightDown:
            case WmMiddleDown:
            case WmXDown:
                Woken(lParam);
                return null;
            case WmRightUp:
            case WmMiddleUp:
            case WmXUp:
                // Not while another button is still down: its release is the shield's too.
                if (_press is { Clicks: false } && !IsAnyButtonDown(wParam))
                {
                    LetGoOfPress();
                    LeaveIfAsked();
                }
                return null;
            case WmNcDestroy:
                Forget(window);
                return null;
            default:
                return null;
        }
    }

    // The mouse stays the shield's until the release, wherever the pointer goes meanwhile.
    private void Pressed(bool isSecond, nint lParam)
    {
        var pointer = PointerOf(GetMessageExtraInfo());
        _press = new Press(pointer, isSecond, XOf(lParam), YOf(lParam), Clicks: true);
        _ = SetCapture(Window);
        _owner.Pressed(pointer);
    }

    // Another button: told, and left to Windows, and no click comes of it. The shield stays for its release as for the
    // left button's: gone at once, it would leave WinUI a release without its press, and WinUI then set the cursor it
    // last knew, the hidden one, where the arrow was due.
    private void Woken(nint lParam)
    {
        var pointer = PointerOf(GetMessageExtraInfo());
        if (_press is null)
        {
            _press = new Press(pointer, IsSecond: false, XOf(lParam), YOf(lParam), Clicks: false);
            _ = SetCapture(Window);
        }
        _owner.Woken(pointer);
    }

    // Whether a mouse message says a button is down still: MK_LBUTTON, MK_RBUTTON, MK_MBUTTON, MK_XBUTTON1, MK_XBUTTON2.
    private static bool IsAnyButtonDown(nint wParam) => (wParam & 0x0073) != 0;

    private void LetGoOfPress()
    {
        _press = null;
        _isLettingGo = true;
        if (GetCapture() == Window)
        {
            _ = ReleaseCapture();
        }
        _isLettingGo = false;
    }

    private void Released(nint wParam, nint lParam)
    {
        if (_press is not { Clicks: true } press)
        {
            return;
        }
        int dpi = GetDpiForWindow(Window);
        int reach = ClickReach * (dpi > 0 ? dpi : DefaultDpi) / DefaultDpi;
        bool isClick = Math.Abs(XOf(lParam) - press.X) <= reach && Math.Abs(YOf(lParam) - press.Y) <= reach;
        if (IsAnyButtonDown(wParam))
        {
            // Another button is still down: the shield stays for its release, and this one's click is told meanwhile.
            _press = press with { Clicks = false };
            if (isClick)
            {
                _owner.Clicked(press.Pointer, press.IsSecond);
            }
            return;
        }
        // Over from here on: an owner that uncovers the picture in its click takes the shield away at once.
        LetGoOfPress();
        if (isClick)
        {
            _owner.Clicked(press.Pointer, press.IsSecond);
        }
        LeaveIfAsked();
    }

    private void EndPress()
    {
        _press = null;
        LeaveIfAsked();
    }

    // The picture was uncovered during the press, and not covered again since.
    private void LeaveIfAsked()
    {
        if (_leavesWithRelease && IsUp)
        {
            Leave();
        }
    }

    private static int XOf(nint lParam) => unchecked((short)(lParam & 0xFFFF));

    private static int YOf(nint lParam) => unchecked((short)((lParam >> 16) & 0xFFFF));

    private static ushort Register()
    {
        fixed (char* name = "GranaryCursorShield")
        {
            var windowClass = new WindowClassEx
            {
                Size = (uint)sizeof(WindowClassEx),
                Style = ClassDoubleClicks,
                Procedure = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&OnMessage,
                Instance = GetModuleHandleW(0),
                ClassName = (nint)name,
            };
            return RegisterClassExW(ref windowClass);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint OnMessage(nint window, uint message, nint wParam, nint lParam)
    {
        try
        {
            // Not known yet while Windows makes the window, nor after it is forgotten.
            if (Shields.TryGetValue(window, out var weak))
            {
                if (!weak.TryGetTarget(out var shield))
                {
                    // The shield is gone without having been disposed: its window must not stay over the picture.
                    Shields.TryRemove(new KeyValuePair<nint, WeakReference<CursorShield>>(window, weak));
                    _ = DestroyWindow(window);
                    return 0;
                }
                if (shield.Answer(window, message, wParam, lParam) is { } answer)
                {
                    return answer;
                }
            }
        }
        catch (Exception ex)
        {
            // No managed exception may cross the native window procedure; Windows then answers for the window.
            System.Diagnostics.Debug.WriteLine($"The cursor shield's message failed: {ex.GetType().Name}");
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    // A button held on the shield: the left one, whose release is a click, or another one, whose release is none.
    private readonly record struct Press(ShieldPointer Pointer, bool IsSecond, int X, int Y, bool Clicks);

    [LibraryImport("user32.dll")]
    private static partial ushort RegisterClassExW(ref WindowClassEx windowClass);

    [LibraryImport("user32.dll")]
    private static partial nint CreateWindowExW(uint exStyle, nint className, nint title, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    private static partial nint SetCursor(nint cursor);

    [LibraryImport("user32.dll")]
    private static partial nint SetCapture(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetCapture();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReleaseCapture();

    [LibraryImport("user32.dll")]
    private static partial nint GetMessageExtraInfo();

    [LibraryImport("user32.dll")]
    private static partial int GetDpiForWindow(nint window);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetModuleHandleW(nint name);

    // WNDCLASSEXW.
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public nint Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public nint MenuName;
        public nint ClassName;
        public nint SmallIcon;
    }
}
