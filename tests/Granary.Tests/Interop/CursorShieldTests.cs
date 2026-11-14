using System.Runtime.InteropServices;
using Granary.App.Interop;

namespace Granary.Tests.Interop;

// Real windows and window messages exercise the native boundary without starting WinUI, showing a window or moving the
// mouse: the parent is never shown, so nothing of this reaches the screen.
public sealed partial class CursorShieldTests
{
    private const uint WmSetCursor = 0x0020;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLeftDown = 0x0201;
    private const uint WmLeftUp = 0x0202;
    private const uint WmLeftDouble = 0x0203;
    private const uint WmXDown = 0x020B;
    private const int StyleIndex = -16;
    private const long StyleVisible = 0x10000000;

    // What Windows puts into a mouse message it makes from a finger or a pen.
    private static readonly nint FromTouch = unchecked((nint)0xFF515780L);
    private static readonly nint FromPen = unchecked((nint)0xFF515700L);

    // WM_SETCURSOR's lParam as a mouse move over the client area sends it.
    private const nint CursorRequestForMove = (0x0200 << 16) | 1;

    [Fact]
    public void WhileItCoversThePicture_EveryRequestForTheCursor_GetsTheHiddenOne()
    {
        using var stage = new Stage();
        Assert.True(stage.Shield.Cover(10, 20, 300, 200));
        nint previous = GetCursor();
        try
        {
            foreach (nint shape in new nint[] { 32512, 32649 }) // The arrow and the hand.
            {
                SetCursor(LoadCursorW(0, shape));

                nint answer = SendMessageW(stage.Shield.Window, WmSetCursor, stage.Shield.Window, CursorRequestForMove);

                Assert.Equal(1, answer);
                Assert.Equal(stage.HiddenCursor, GetCursor());
            }
        }
        finally
        {
            SetCursor(previous);
        }
    }

    [Fact]
    public void Covering_ShowsTheShieldOverThatPlaceOfItsParent_AndUncoveringTakesItAway()
    {
        using var stage = new Stage();
        Assert.False(stage.Shield.IsUp);

        Assert.True(stage.Shield.Cover(10, 20, 300, 200));

        Assert.True(stage.Shield.IsUp);
        Assert.True(IsShown(stage.Shield.Window));
        Assert.Equal(stage.Parent, GetParent(stage.Shield.Window));
        Assert.Equal((10, 20, 300, 200), PlaceIn(stage.Parent, stage.Shield.Window));
        // The whole of it is the picture: no border to size by, no title to drag.
        Assert.Equal(1, SendMessageW(stage.Shield.Window, WmNcHitTest, 0, 0));

        stage.Shield.Uncover();

        Assert.False(stage.Shield.IsUp);
        Assert.False(IsShown(stage.Shield.Window));
    }

    [Fact]
    public void CoveringAnotherPlace_LaysANewWindowThere_MadeAtThatPlace()
    {
        // Windows finds a layered window without a surface under the pointer only where it was when it was made: one made
        // empty, or made elsewhere, and sized later is passed over. So the shield is made anew at each place.
        using var stage = new Stage();
        stage.Shield.Cover(10, 20, 300, 200);
        nint first = stage.Shield.Window;

        Assert.True(stage.Shield.Cover(0, 0, 640, 360));

        Assert.NotEqual(first, stage.Shield.Window);
        Assert.False(IsWindow(first));
        Assert.True(stage.Shield.IsUp);
        Assert.True(IsShown(stage.Shield.Window));
        Assert.Equal((0, 0, 640, 360), PlaceIn(stage.Parent, stage.Shield.Window));
    }

    [Fact]
    public void CoveredAgainAtTheSamePlace_AfterItWasUncovered_TheShieldIsTheSameWindow()
    {
        using var stage = new Stage();
        stage.Shield.Cover(10, 20, 300, 200);
        nint first = stage.Shield.Window;
        stage.Shield.Uncover();

        Assert.True(stage.Shield.Cover(10, 20, 300, 200));

        Assert.Equal(first, stage.Shield.Window);
        Assert.True(IsShown(first));
    }

    [Fact]
    public void BeforeItIsFirstLaid_TheShieldHasNoWindow()
    {
        using var stage = new Stage();

        Assert.Equal(0, stage.Shield.Window);
        Assert.False(stage.Shield.IsUp);
        stage.Shield.Uncover();
    }

    [Fact]
    public void LaidElsewhereDuringAPress_TheShieldStaysForTheRelease_AndMovesWhenLaidAgain()
    {
        // The picture changing its size by a pixel during a click must not cost the click.
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);
        nint first = stage.Shield.Window;
        SendMessageW(first, WmLeftDown, 1, At(50, 60));

        Assert.True(stage.Shield.Cover(0, 0, 640, 360));

        Assert.Equal(first, stage.Shield.Window);
        SendMessageW(first, WmLeftUp, 0, At(50, 60));
        Assert.Equal(["pressed Mouse", "clicked Mouse"], stage.Owner.Seen);
        Assert.True(stage.Shield.Cover(0, 0, 640, 360));
        Assert.NotEqual(first, stage.Shield.Window);
        Assert.Equal((0, 0, 640, 360), PlaceIn(stage.Parent, stage.Shield.Window));
    }

    [Fact]
    public void CoveringTheSamePlaceAgain_ChangesNoWindow()
    {
        // Every change of a window has Windows work out the cursor again, so the shield is left alone while it is right.
        using var stage = new Stage();
        stage.Shield.Cover(10, 20, 300, 200);
        nint other = CreateWindowExW(0, "STATIC", null, 0x40000000, 0, 0, 5, 5, stage.Parent, 0, 0, 0);
        Assert.True(SetWindowPos(other, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010));
        Assert.Equal(other, GetWindow(stage.Parent, 5));

        Assert.True(stage.Shield.Cover(10, 20, 300, 200));

        Assert.Equal(other, GetWindow(stage.Parent, 5)); // Still the topmost child: the shield was not raised again.
    }

    [Fact]
    public void NothingToCover_TakesTheShieldAway()
    {
        using var stage = new Stage();
        stage.Shield.Cover(10, 20, 300, 200);

        Assert.False(stage.Shield.Cover(10, 20, 0, 200));

        Assert.False(stage.Shield.IsUp);
        Assert.False(IsShown(stage.Shield.Window));
    }

    [Fact]
    public void AMouseMove_IsHandedToTheOwner_WhoTellsARealOneFromOneWindowsMadeUp()
    {
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);

        SendMessageW(stage.Shield.Window, WmMouseMove, 0, At(50, 60));

        Assert.Equal(["mouse seen"], stage.Owner.Seen);
    }

    [Fact]
    public void TheMoveWindowsMakesToWhereAFingerGoesDown_IsNoMove()
    {
        // Windows moves its pointer to where a finger goes down, before the press: that would show the controls, and the
        // tap that follows would hide them again.
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);

        SendFrom(FromTouch, stage.Shield.Window, WmMouseMove, 0, At(50, 60));

        Assert.Empty(stage.Owner.Seen);
    }

    [Fact]
    public void AFingerThatDrags_IsHandedToTheOwner_OnceItGoesFurtherThanATap()
    {
        // Windows makes mouse messages of a finger's drag, as of its tap: the drag shows the cursor and the controls, as a
        // finger's move over WinUI does.
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 600, 400);

        SendFrom(FromTouch, stage.Shield.Window, WmMouseMove, 0, At(50, 60));
        SendFrom(FromTouch, stage.Shield.Window, WmLeftDown, 1, At(50, 60));
        SendFrom(FromTouch, stage.Shield.Window, WmMouseMove, 1, At(55, 62));
        SendFrom(FromTouch, stage.Shield.Window, WmMouseMove, 1, At(250, 60));
        SendFrom(FromTouch, stage.Shield.Window, WmLeftUp, 0, At(250, 60));

        Assert.Equal(["pressed Touch", "touch moved"], stage.Owner.Seen);
    }

    [Fact]
    public void AFingerThatDragsAndComesBack_IsNoTap()
    {
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 600, 400);

        SendFrom(FromTouch, stage.Shield.Window, WmLeftDown, 1, At(50, 60));
        SendFrom(FromTouch, stage.Shield.Window, WmMouseMove, 1, At(250, 60));
        SendFrom(FromTouch, stage.Shield.Window, WmMouseMove, 1, At(52, 60));
        SendFrom(FromTouch, stage.Shield.Window, WmLeftUp, 0, At(52, 60));

        Assert.Equal(["pressed Touch", "touch moved", "touch moved"], stage.Owner.Seen);
    }

    [Fact]
    public void AFingersTapThatMovesALittle_IsATapAlone()
    {
        // A finger moves a little as it taps: a move then would show the controls, and the tap would hide them again.
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);

        SendFrom(FromTouch, stage.Shield.Window, WmLeftDown, 1, At(50, 60));
        SendFrom(FromTouch, stage.Shield.Window, WmMouseMove, 1, At(53, 62));
        SendFrom(FromTouch, stage.Shield.Window, WmLeftUp, 0, At(53, 62));

        Assert.Equal(["pressed Touch", "clicked Touch"], stage.Owner.Seen);
    }

    [Fact]
    public void APensMove_IsHandedToTheOwner_AsTheMouses()
    {
        // A pen that hovers moves the pointer, as a mouse does.
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);

        SendFrom(FromPen, stage.Shield.Window, WmMouseMove, 0, At(50, 60));

        Assert.Equal(["mouse seen"], stage.Owner.Seen);
    }

    [Fact]
    public void AnOwnerThatFails_DoesNotBreakTheWindow()
    {
        // No exception may cross the window procedure: Windows then answers for the window, and the next message is taken.
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);
        stage.Owner.OnClicked = () => throw new InvalidOperationException("the owner failed");

        SendMessageW(stage.Shield.Window, WmLeftDown, 1, At(50, 60));
        SendMessageW(stage.Shield.Window, WmLeftUp, 0, At(50, 60));
        stage.Owner.OnClicked = null;
        SendMessageW(stage.Shield.Window, WmLeftDown, 1, At(50, 60));
        SendMessageW(stage.Shield.Window, WmLeftUp, 0, At(50, 60));

        Assert.Equal(["pressed Mouse", "clicked Mouse", "pressed Mouse", "clicked Mouse"], stage.Owner.Seen);
        Assert.NotEqual(stage.Shield.Window, GetCapture());
    }

    [Fact]
    public void AnOwnerThatDisposesTheShieldInItsClick_LeavesNoWindow()
    {
        // The mouse's back button, or a click that closes the player, ends the page inside the shield's own message.
        var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);
        nint window = stage.Shield.Window;
        stage.Owner.OnClicked = stage.Shield.Dispose;
        try
        {
            SendMessageW(window, WmLeftDown, 1, At(50, 60));
            SendMessageW(window, WmLeftUp, 0, At(50, 60));

            Assert.False(IsWindow(window));
            Assert.False(stage.Shield.IsUp);
            Assert.Equal(0, stage.Shield.Window);
        }
        finally
        {
            stage.Dispose();
        }
    }

    [Fact]
    public void AClick_IsAPressAndARelease_AtOnePlace()
    {
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);

        SendMessageW(stage.Shield.Window, WmLeftDown, 1, At(50, 60));
        Assert.Equal(["pressed Mouse"], stage.Owner.Seen);
        SendMessageW(stage.Shield.Window, WmLeftUp, 0, At(52, 61));

        Assert.Equal(["pressed Mouse", "clicked Mouse"], stage.Owner.Seen);
    }

    [Fact]
    public void ADoubleClick_IsAClick_AndThenASecondOne()
    {
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);

        SendMessageW(stage.Shield.Window, WmLeftDown, 1, At(50, 60));
        SendMessageW(stage.Shield.Window, WmLeftUp, 0, At(50, 60));
        SendMessageW(stage.Shield.Window, WmLeftDouble, 1, At(50, 60));
        SendMessageW(stage.Shield.Window, WmLeftUp, 0, At(50, 60));

        Assert.Equal(["pressed Mouse", "clicked Mouse", "pressed Mouse", "clicked Mouse again"], stage.Owner.Seen);
    }

    [Fact]
    public void AReleaseFarFromThePress_IsNoClick()
    {
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 600, 400);

        SendMessageW(stage.Shield.Window, WmLeftDown, 1, At(50, 60));
        SendMessageW(stage.Shield.Window, WmLeftUp, 0, At(450, 60));

        Assert.Equal(["pressed Mouse"], stage.Owner.Seen);
    }

    [Fact]
    public void AReleaseWithoutItsPress_IsNoClick()
    {
        // The button went down somewhere else, before the shield came.
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);

        SendMessageW(stage.Shield.Window, WmLeftUp, 0, At(50, 60));

        Assert.Empty(stage.Owner.Seen);
    }

    [Theory]
    [InlineData(true, "Touch")]
    [InlineData(false, "Pen")]
    public void ATapOfAFingerOrAPen_IsSaidAsSuch(bool isTouch, string pointer)
    {
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);

        nint source = isTouch ? FromTouch : FromPen;
        SendFrom(source, stage.Shield.Window, WmLeftDown, 1, At(50, 60));
        SendFrom(source, stage.Shield.Window, WmLeftUp, 0, At(50, 60));

        Assert.Equal([$"pressed {pointer}", $"clicked {pointer}"], stage.Owner.Seen);
    }

    [Theory]
    [InlineData(0L, "Mouse")]
    [InlineData(0x1234L, "Mouse")]
    [InlineData(0xFF515780L, "Touch")]
    [InlineData(0xFF5157FFL, "Touch")]
    [InlineData(0xFF515700L, "Pen")]
    [InlineData(0xFF51577FL, "Pen")]
    public void WhatWindowsNotesWithAMouseMessage_SaysWhatMadeIt(long extraInfo, string expected) =>
        Assert.Equal(expected, CursorShield.PointerOf((nint)extraInfo).ToString());

    [Fact]
    public void UncoveredDuringAPress_TheShieldStaysForTheRelease_SoThatTheClickIsNotLost()
    {
        // A hand that clicks also moves the mouse a little: the move shows the cursor, which uncovers the picture. Gone at
        // once, the shield would leave the release to WinUI, which never saw the press.
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);
        SendMessageW(stage.Shield.Window, WmLeftDown, 1, At(50, 60));

        stage.Shield.Uncover();

        Assert.True(IsShown(stage.Shield.Window));
        Assert.Equal(stage.Shield.Window, GetCapture());
        SendMessageW(stage.Shield.Window, WmLeftUp, 0, At(51, 60));
        Assert.Equal(["pressed Mouse", "clicked Mouse"], stage.Owner.Seen);
        Assert.False(stage.Shield.IsUp);
        Assert.False(IsShown(stage.Shield.Window));
        Assert.NotEqual(stage.Shield.Window, GetCapture());
    }

    [Fact]
    public void CoveredAgainDuringAPress_TheShieldStaysAfterTheRelease()
    {
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);
        SendMessageW(stage.Shield.Window, WmLeftDown, 1, At(50, 60));
        stage.Shield.Uncover();

        stage.Shield.Cover(0, 0, 300, 200);
        SendMessageW(stage.Shield.Window, WmLeftUp, 0, At(50, 60));

        Assert.True(stage.Shield.IsUp);
        Assert.True(IsShown(stage.Shield.Window));
    }

    [Fact]
    public void AnOwnerThatUncoversInItsClick_TakesTheShieldAwayAtOnce()
    {
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);
        stage.Owner.OnClicked = stage.Shield.Uncover;

        SendMessageW(stage.Shield.Window, WmLeftDown, 1, At(50, 60));
        SendMessageW(stage.Shield.Window, WmLeftUp, 0, At(50, 60));

        Assert.False(stage.Shield.IsUp);
        Assert.False(IsShown(stage.Shield.Window));
    }

    [Fact]
    public void APressWhoseMouseIsTakenAway_IsNoClick()
    {
        // Another window took the mouse while the button was down (a menu opening, the window losing the foreground).
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);
        SendMessageW(stage.Shield.Window, WmLeftDown, 1, At(50, 60));
        stage.Shield.Uncover();

        SetCapture(stage.Parent);
        ReleaseCapture();
        SendMessageW(stage.Shield.Window, WmLeftUp, 0, At(50, 60));

        Assert.Equal(["pressed Mouse"], stage.Owner.Seen);
        Assert.False(stage.Shield.IsUp);
    }

    [Fact]
    public void TheMousesBackButton_IsHandedToTheOwner()
    {
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);

        nint back = SendMessageW(stage.Shield.Window, WmXDown, 1 << 16, At(50, 60));

        Assert.Equal(1, back);
        Assert.Equal(["back pressed"], stage.Owner.Seen);
    }

    [Theory]
    [InlineData(0x0204u, 0)] // The right button.
    [InlineData(0x0207u, 0)] // The middle button.
    [InlineData(0x020Bu, 2)] // The mouse's forward button.
    public void AnotherButtonOfTheMouse_WakesTheOwner_AndClicksNothing(uint message, int extraButton)
    {
        // The pointer is in use: the cursor is to show. But only the left button clicks what lies under the shield.
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);

        SendMessageW(stage.Shield.Window, message, extraButton << 16, At(50, 60));
        SendMessageW(stage.Shield.Window, message + 1, extraButton << 16, At(50, 60)); // Its release.

        Assert.Equal(["woken Mouse"], stage.Owner.Seen);
        Assert.NotEqual(stage.Shield.Window, GetCapture());
    }

    [Theory]
    [InlineData(0x0204u, 0)]
    [InlineData(0x0207u, 0)]
    [InlineData(0x020Bu, 2)]
    public void UncoveredByAnotherButtonsPress_TheShieldStaysForItsRelease(uint message, int extraButton)
    {
        // The press shows the cursor, which uncovers the picture. Gone at once, the shield would leave WinUI the release
        // without its press: WinUI then set the cursor it last knew, the hidden one, and the cursor did not come back.
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);
        stage.Owner.OnWoken = stage.Shield.Uncover;

        SendMessageW(stage.Shield.Window, message, extraButton << 16, At(50, 60));

        Assert.True(IsShown(stage.Shield.Window));
        Assert.Equal(stage.Shield.Window, GetCapture());
        SendMessageW(stage.Shield.Window, message + 1, extraButton << 16, At(50, 60));
        Assert.False(stage.Shield.IsUp);
        Assert.False(IsShown(stage.Shield.Window));
        Assert.NotEqual(stage.Shield.Window, GetCapture());
        Assert.Equal(["woken Mouse"], stage.Owner.Seen);
    }

    [Theory]
    [InlineData(0x0204u, 0x0002, 0x0207u, 0x0010)] // The right button held, the middle one pressed and let go.
    [InlineData(0x0207u, 0x0010, 0x0204u, 0x0002)] // The middle button held, the right one pressed and let go.
    public void WithTwoButtonsDown_TheShieldStaysUntilTheLastOneIsLetGo(uint firstDown, int firstFlag, uint secondDown, int secondFlag)
    {
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);
        stage.Owner.OnWoken = stage.Shield.Uncover;
        SendMessageW(stage.Shield.Window, firstDown, firstFlag, At(50, 60));
        SendMessageW(stage.Shield.Window, secondDown, firstFlag | secondFlag, At(50, 60));

        SendMessageW(stage.Shield.Window, secondDown + 1, firstFlag, At(50, 60));

        Assert.True(IsShown(stage.Shield.Window));
        Assert.Equal(stage.Shield.Window, GetCapture());
        SendMessageW(stage.Shield.Window, firstDown + 1, 0, At(50, 60));
        Assert.False(IsShown(stage.Shield.Window));
        Assert.NotEqual(stage.Shield.Window, GetCapture());
    }

    [Fact]
    public void ALeftClickWhileAnotherButtonIsDown_Clicks_AndTheShieldStaysForThatButton()
    {
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);
        SendMessageW(stage.Shield.Window, WmLeftDown, 0x0001, At(50, 60));
        SendMessageW(stage.Shield.Window, 0x0204, 0x0003, At(50, 60));
        stage.Shield.Uncover();

        SendMessageW(stage.Shield.Window, WmLeftUp, 0x0002, At(50, 60));

        Assert.Equal(["pressed Mouse", "woken Mouse", "clicked Mouse"], stage.Owner.Seen);
        Assert.True(IsShown(stage.Shield.Window));
        SendMessageW(stage.Shield.Window, 0x0205, 0, At(50, 60));
        Assert.False(IsShown(stage.Shield.Window));
    }

    [Fact]
    public void TheLeftButtonsReleaseDuringAnotherButtonsPress_IsNoClick()
    {
        using var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);
        SendMessageW(stage.Shield.Window, 0x0204, 0, At(50, 60));

        SendMessageW(stage.Shield.Window, WmLeftUp, 0, At(50, 60));

        Assert.Equal(["woken Mouse"], stage.Owner.Seen);
    }

    [Fact]
    public void Disposed_TheShieldIsGone_AndCoversNothingMore()
    {
        var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);
        nint window = stage.Shield.Window;
        try
        {
            stage.Shield.Dispose();

            Assert.False(IsWindow(window));
            Assert.False(stage.Shield.IsUp);
            Assert.False(stage.Shield.Cover(0, 0, 300, 200));
            stage.Shield.Uncover();
            stage.Shield.Dispose();
        }
        finally
        {
            stage.Dispose();
        }
    }

    [Fact]
    public void WithItsParentGone_TheShieldCoversNothingMore()
    {
        // The main window closes with the player open: Windows takes the shield with it.
        var stage = new Stage();
        stage.Shield.Cover(0, 0, 300, 200);

        DestroyWindow(stage.Parent);

        Assert.False(stage.Shield.IsUp);
        Assert.False(stage.Shield.Cover(0, 0, 300, 200));
        stage.Dispose();
    }

    private static nint At(int x, int y) => (y << 16) | (x & 0xFFFF);

    // A message as Windows makes it from a finger or a pen: with their mark in the thread's extra info, taken back after.
    private static void SendFrom(nint source, nint window, uint message, nint wParam, nint lParam)
    {
        SetMessageExtraInfo(source);
        try
        {
            SendMessageW(window, message, wParam, lParam);
        }
        finally
        {
            SetMessageExtraInfo(0);
        }
    }

    private static bool IsShown(nint window) => (GetWindowLongPtrW(window, StyleIndex) & StyleVisible) != 0;

    private static (int X, int Y, int Width, int Height) PlaceIn(nint parent, nint child)
    {
        Assert.True(GetWindowRect(child, out var rect));
        var corner = new Point { X = rect.Left, Y = rect.Top };
        Assert.True(ScreenToClient(parent, ref corner));
        return (corner.X, corner.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    // A hidden window to hold the shield, a cursor that stands in for the blank one, and what the shield told its owner.
    private sealed class Stage : IDisposable
    {
        public Stage()
        {
            Parent = CreateWindowExW(0, "STATIC", "Shield test", 0, 0, 0, 800, 600, 0, 0, 0, 0);
            Assert.NotEqual(0, Parent);
            HiddenCursor = LoadCursorW(0, 32515); // IDC_CROSS: a distinct handle stands in for the blank one.
            Shield = new CursorShield(Parent, HiddenCursor, Owner);
        }

        public nint Parent { get; }

        public nint HiddenCursor { get; }

        public Told Owner { get; } = new();

        public CursorShield Shield { get; }

        public void Dispose()
        {
            Shield.Dispose();
            if (IsWindow(Parent))
            {
                DestroyWindow(Parent);
            }
        }
    }

    private sealed class Told : ICursorShieldOwner
    {
        public List<string> Seen { get; } = [];

        public Action? OnClicked { get; set; }

        public void MouseSeen() => Seen.Add("mouse seen");

        public void TouchMoved() => Seen.Add("touch moved");

        public void Pressed(ShieldPointer pointer) => Seen.Add($"pressed {pointer}");

        public void Clicked(ShieldPointer pointer, bool isSecond)
        {
            Seen.Add($"clicked {pointer}{(isSecond ? " again" : "")}");
            OnClicked?.Invoke();
        }

        public void BackPressed() => Seen.Add("back pressed");

        public Action? OnWoken { get; set; }

        public void Woken(ShieldPointer pointer)
        {
            Seen.Add($"woken {pointer}");
            OnWoken?.Invoke();
        }
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string? title, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nint GetCursor();

    [LibraryImport("user32.dll")]
    private static partial nint SetCursor(nint cursor);

    [LibraryImport("user32.dll")]
    private static partial nint LoadCursorW(nint instance, nint name);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint window, int index);

    [LibraryImport("user32.dll")]
    private static partial nint GetParent(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint window, uint relation);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(nint window, ref Point point);

    [LibraryImport("user32.dll")]
    private static partial nint SetMessageExtraInfo(nint info);

    [LibraryImport("user32.dll")]
    private static partial nint GetCapture();

    [LibraryImport("user32.dll")]
    private static partial nint SetCapture(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReleaseCapture();

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}
