using Granary.App.Controls;
using Granary.App.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Views;

// WinUI does not keep a hidden cursor. Whenever Windows works the cursor out again without the mouse having moved, as it
// does when any window changes, WinUI shows the arrow, and nothing set through it or with SetCursor shows until the mouse
// really moves (microsoft-ui-xaml issue 11942): a hidden cursor came back by itself, a moment after the controls went or
// minutes into a pause. So while the cursor is hidden, a window of Granary's own lies over the picture (CursorShield).
// Windows asks that one for the cursor, each time, and draws the hidden one. The pointer is then no longer WinUI's, and
// the page does here what WinUI did with it: a real move or a press shows the cursor and the controls, and a click or a
// tap goes to what lies under it.
public sealed partial class PlayerPage : ICursorShieldOwner
{
    // How many times, for each time the shield was laid, the log says that Windows drew a cursor over it all the same.
    private const int ShieldFaultLogLimit = 3;

    private CursorShield? _shield;
    private (int X, int Y, int Width, int Height) _shieldPlace;
    private bool _isShieldRefusalSaid;
    private int _shieldFaultsLogged;

    // What WinUI had under the pointer as a press on the shield began: the click goes to that, as WinUI gives its own to
    // the element the press began on. By the release the controls may have come up under the pointer, since a hand that
    // clicks also moves the mouse.
    private UIElement? _shieldPressTarget;

    // The shield lies over the picture exactly while the cursor is hidden in the full player, wherever the pointer is: the
    // cursor only hides with the pointer over the picture, and the pointer leaves it only by a real move, which shows the
    // cursor. Laid again whenever the picture may have moved: as the page or the window changes its size, with the
    // window's mode, and at every look (CursorLookInterval). The same place again changes nothing.
    private void KeepShield()
    {
        if (ShieldPlace() is not { } place)
        {
            if (_shield is { IsUp: true } up)
            {
                up.Uncover();
                if (!up.IsUp)
                {
                    _autoHideLog.LogDebug("The shield over the player's picture is gone");
                }
            }
            return;
        }
        _shield ??= new CursorShield(Win32Interop.GetWindowFromWindowId(_appWindow!.Id), BlankCursor.Handle, this);
        bool wasUp = _shield.IsUp;
        if (_shield.Cover(place.X, place.Y, place.Width, place.Height))
        {
            _shieldPlace = place;
            if (!wasUp)
            {
                _shieldFaultsLogged = 0;
                _autoHideLog.LogDebug("The player's cursor is hidden: the shield lies over the picture, {Width}x{Height} at {X},{Y}",
                    place.Width, place.Height, place.X, place.Y);
            }
        }
        else if (!_isShieldRefusalSaid)
        {
            _isShieldRefusalSaid = true;
            _autoHideLog.LogWarning("The shield over the player's picture could not be laid; a hidden cursor can come back until the mouse moves");
        }
    }

    // The picture in the window's client pixels, or null while there is nothing to shield: the page, without the strip at
    // its top where a window has its title bar, which is Windows' to drag and has an input window of WinUI's own over it.
    // Not in the bar's view: its picture is a button. Not while a dialog, a menu or a flyout is open either: one inside
    // the window would lie under the shield, and what is in it is WinUI's to press. Seen at the next look.
    private (int X, int Y, int Width, int Height)? ShieldPlace()
    {
        if (!_isCursorHidden || !IsLoaded || IsDocked || BlankCursor.Handle == 0 || _appWindow is null || XamlRoot is not { } root
            || HasOpenMenu())
        {
            return null;
        }
        var origin = TransformToVisual(null).TransformPoint(default);
        double scale = root.RasterizationScale;
        double strip = IsFullScreen ? 0 : TitleBarHeight;
        int width = (int)Math.Round(ActualWidth * scale);
        int height = (int)Math.Round((ActualHeight - strip) * scale);
        if (width <= 0 || height <= 0)
        {
            return null;
        }
        return ((int)Math.Round(origin.X * scale), (int)Math.Round((origin.Y + strip) * scale), width, height);
    }

    private void DropShield()
    {
        _shield?.Dispose();
        _shield = null;
        _shieldPressTarget = null;
    }

    // For the log, should a hidden cursor ever come back all the same: Windows drawing one of its own cursors where the
    // shield lies, and which window it had under the pointer then.
    private void CheckShield()
    {
        if (_shield is not { IsUp: true } shield || _shieldFaultsLogged >= ShieldFaultLogLimit
            || !PointerOverWindow(out var cursor, out nint window) || !DrawsSystemCursor())
        {
            return;
        }
        var inWindow = cursor;
        if (!ScreenToClient(window, ref inWindow) || inWindow.X < _shieldPlace.X || inWindow.X >= _shieldPlace.X + _shieldPlace.Width
            || inWindow.Y < _shieldPlace.Y || inWindow.Y >= _shieldPlace.Y + _shieldPlace.Height)
        {
            return;
        }
        _shieldFaultsLogged++;
        nint under = WindowFromPoint(cursor);
        _autoHideLog.LogDebug(
            "Windows draws one of its own cursors over the player's picture although the shield lies there (under the pointer: {Window}; Granary in front: {Front}; full screen: {FullScreen})",
            under == shield.Window ? "the shield" : ClassOf(under), GetAncestor(GetForegroundWindow(), GaRoot) == window, IsFullScreen);
    }

    // Whether Windows draws one of its own cursors now (the arrow, the hand and the rest): the blank one is none of them.
    private static bool DrawsSystemCursor()
    {
        var info = new CursorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || (info.Flags & CursorShowing) == 0 || info.Cursor == 0)
        {
            return false;
        }
        s_systemCursors ??= [.. SystemCursorIds.Select(id => LoadCursorW(0, id)).Where(handle => handle != 0)];
        return Array.IndexOf(s_systemCursors, info.Cursor) >= 0;
    }

    // A window by its class, for the log: which part of the window it is. Never a title.
    private static unsafe string ClassOf(nint window)
    {
        if (window == 0)
        {
            return "none";
        }
        char* name = stackalloc char[128];
        int length = GetClassNameW(window, name, 128);
        return length > 0 ? new string(name, 0, length) : "?";
    }

    // A pen's move is measured as the mouse's: Windows moves its pointer with the pen.
    void ICursorShieldOwner.MouseSeen() => OnShield("a mouse move", static page => page.PointerSeen(PointerDeviceType.Mouse));

    // What is under the pointer is noted first: the press shows the controls, which then lie under it.
    void ICursorShieldOwner.Pressed(ShieldPointer pointer) => OnShield("a press", page =>
    {
        page._shieldPressTarget = page.ElementAtPointer();
        page.PressedOnPage(DeviceOf(pointer));
        page._cursorWindow?.NotePointerPress();
    });

    void ICursorShieldOwner.Clicked(ShieldPointer pointer, bool isSecond) =>
        OnShield("a click", page => page.ClickedOnShield(DeviceOf(pointer), isSecond));

    void ICursorShieldOwner.BackPressed() => OnShield("the mouse's back button", static page => page._cursorWindow?.GoBackByMouse());

    // Called inside the shield's window message: nothing may escape.
    private void OnShield(string what, Action<PlayerPage> act)
    {
        try
        {
            act(this);
        }
        catch (Exception ex)
        {
            _autoHideLog.LogError(ex, "The player could not take {What} on its cursor shield", what);
        }
    }

    private static PointerDeviceType DeviceOf(ShieldPointer pointer) => pointer switch
    {
        ShieldPointer.Touch => PointerDeviceType.Touch,
        ShieldPointer.Pen => PointerDeviceType.Pen,
        _ => PointerDeviceType.Mouse,
    };

    // A click or a tap goes to what WinUI had under the pointer as it began, as if the shield were not there. The picture
    // takes it as it takes WinUI's. A button is pressed: the skip pill and the next episode's card show without the
    // controls. Anything else cannot be handed a click from here, and the viewer aimed at it without seeing the cursor:
    // the controls show with the cursor, and the next click is WinUI's.
    private void ClickedOnShield(PointerDeviceType device, bool isSecond)
    {
        if (device != PointerDeviceType.Mouse)
        {
            // A finger or a pen shows the cursor, as its move over WinUI does: what it touches next is WinUI's again.
            _cursor.TouchOrPenMoves();
            ApplyCursor();
        }
        var target = _shieldPressTarget;
        _shieldPressTarget = null;
        if (ReferenceEquals(target, Surface))
        {
            Focus(FocusState.Pointer);
            if (isSecond)
            {
                SurfaceDoubleTapped(device);
            }
            else
            {
                SurfaceTapped(device);
            }
        }
        else if (ButtonOver(target) is { } button)
        {
            Press(button);
        }
        else
        {
            ShowControls();
        }
    }

    // The topmost element that takes input at the pointer, or null off the picture.
    private UIElement? ElementAtPointer()
    {
        if (PointerInPage() is not { } inPage || XamlRoot?.Content is not UIElement content)
        {
            return null;
        }
        var inWindow = TransformToVisual(null).TransformPoint(inPage);
        return VisualTreeHelper.FindElementsInHostCoordinates(inWindow, content, false).FirstOrDefault();
    }

    private static ButtonBase? ButtonOver(DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase { IsEnabled: true } button)
            {
                return button;
            }
        }
        return null;
    }

    // As a screen reader presses it: its Click is raised, or it is switched over.
    private static void Press(ButtonBase button)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(button) ?? FrameworkElementAutomationPeer.CreatePeerForElement(button);
        if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
        {
            invoke.Invoke();
        }
        else if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle)
        {
            toggle.Toggle();
        }
    }
}
