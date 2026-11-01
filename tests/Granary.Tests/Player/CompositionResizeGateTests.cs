using Granary.Player;
using Granary.Tests.Fakes;

namespace Granary.Tests.Player;

/// <summary>
/// The picture hides while mpv's swap chain catches up with a panel that jumped to another size (the player minimized
/// to its bar or brought back, full screen), so the panel never shows the old picture's corner.
/// </summary>
public sealed class CompositionResizeGateTests
{
    private static readonly PixelSize Full = new(1440, 810);
    private static readonly PixelSize Bar = new(160, 90);

    private readonly ManualTimeProvider _time = new();

    [Fact]
    public void APanelThatJumpsFarFromTheBuffer_HidesThePicture()
    {
        var gate = new CompositionResizeGate(_time);

        gate.PanelResized(Bar, buffer: Full);

        Assert.True(gate.IsHidden);
    }

    [Fact]
    public void ASmallStep_AsWhileDraggingTheWindowsEdge_KeepsThePictureShown()
    {
        var gate = new CompositionResizeGate(_time);

        gate.PanelResized(new PixelSize(1460, 820), buffer: Full);

        Assert.False(gate.IsHidden);
    }

    [Fact]
    public void WithoutASwapChain_ThePictureNeverHides()
    {
        var gate = new CompositionResizeGate(_time);

        gate.PanelResized(Bar, buffer: default);

        Assert.False(gate.IsHidden);
    }

    [Fact]
    public void ABufferOfTheNewSize_IsNotEnough_UntilMpvPresentsIntoIt()
    {
        // Until mpv presents again, the screen keeps its last picture, drawn at the old size.
        var gate = new CompositionResizeGate(_time);
        gate.PanelResized(Bar, buffer: Full);

        Assert.False(gate.Frame(buffer: Full, presents: 10));
        Assert.False(gate.Frame(buffer: Bar, presents: 11));
        Assert.False(gate.Frame(buffer: Bar, presents: 11));

        Assert.True(gate.Frame(buffer: Bar, presents: 12));
        Assert.False(gate.IsHidden);
    }

    [Fact]
    public void AnIdleMpv_ShowsThePictureOnceItHasPresentedNothingForTheSettleTime()
    {
        // Paused, mpv draws once after the resize; when that present came before the new size was seen, no other comes.
        var gate = new CompositionResizeGate(_time);
        gate.PanelResized(Full, buffer: Bar);
        Assert.False(gate.Frame(buffer: Full, presents: 20));

        _time.Advance(CompositionResizeGate.SettleTime - TimeSpan.FromMilliseconds(1));
        Assert.False(gate.Frame(buffer: Full, presents: 20));
        _time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.True(gate.Frame(buffer: Full, presents: 20));
    }

    [Fact]
    public void TheSettleTime_CountsFromWhenTheBufferHasTheNewSize()
    {
        var gate = new CompositionResizeGate(_time);
        gate.PanelResized(Bar, buffer: Full);
        _time.Advance(TimeSpan.FromMilliseconds(80));
        Assert.False(gate.Frame(buffer: Full, presents: 2));

        _time.Advance(TimeSpan.FromMilliseconds(20));
        Assert.False(gate.Frame(buffer: Bar, presents: 2));
        _time.Advance(CompositionResizeGate.SettleTime - TimeSpan.FromMilliseconds(1));

        Assert.False(gate.Frame(buffer: Bar, presents: 2));
    }

    [Fact]
    public void ABufferAPixelOff_CountsAsTheNewSize()
    {
        var gate = new CompositionResizeGate(_time);
        gate.PanelResized(Bar, buffer: Full);

        gate.Frame(buffer: new PixelSize(161, 89), presents: 5);

        Assert.True(gate.Frame(buffer: new PixelSize(161, 89), presents: 6));
    }

    [Fact]
    public void ThePicture_ShowsAfterTheLongestWait_EvenIfMpvNeverPresents()
    {
        var gate = new CompositionResizeGate(_time);
        gate.PanelResized(Bar, buffer: Full);

        _time.Advance(CompositionResizeGate.LongestHide - TimeSpan.FromMilliseconds(1));
        Assert.False(gate.Frame(buffer: Full, presents: 3));
        _time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.True(gate.Frame(buffer: Full, presents: 3));
        Assert.False(gate.IsHidden);
    }

    [Fact]
    public void APanelThatJumpsBackWhileHidden_WaitsForAPresentAtTheLatestSize()
    {
        var gate = new CompositionResizeGate(_time);
        gate.PanelResized(Bar, buffer: Full);
        gate.Frame(buffer: Bar, presents: 4);

        gate.PanelResized(Full, buffer: Bar);

        Assert.False(gate.Frame(buffer: Bar, presents: 5));
        Assert.False(gate.Frame(buffer: Full, presents: 6));
        Assert.True(gate.Frame(buffer: Full, presents: 7));
    }

    [Fact]
    public void AFrameWhileShown_ChangesNothing()
    {
        var gate = new CompositionResizeGate(_time);

        Assert.True(gate.Frame(buffer: Full, presents: 1));
        Assert.False(gate.IsHidden);
    }
}
