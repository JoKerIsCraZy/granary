namespace Granary.Player;

/// <summary>
/// Hides the picture while mpv's composition swap chain catches up with a panel that jumped to another size. A
/// SwapChainPanel shows the last presented picture as it is, from its top-left corner. mpv resizes its buffers a little
/// after the panel, on its own thread, and the screen keeps the old picture until mpv presents into them, which can
/// take tens of milliseconds. Meanwhile a panel shrunk to the minimized player's box shows the corner of the old, large
/// picture, and a panel grown to the window shows the small one in its corner. Small steps, as while the window's edge
/// is dragged, keep the picture: hiding it there would flicker.
/// </summary>
public sealed class CompositionResizeGate(TimeProvider time)
{
    /// <summary>The picture shows again after this long even if mpv never presents (nothing loaded, an error).</summary>
    public static readonly TimeSpan LongestHide = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// With the buffer at the new size and no present for this long, mpv is idle (paused) and its last present, drawn
    /// right after the resize, already has the new size. A present after a resize was measured at up to about 50 ms.
    /// </summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(100);

    // A jump: a side grows or shrinks by a quarter or more, as on a move from a 100 % to a 125 % display. Dragging the
    // window's edge moves it by far less per frame.
    private const double JumpRatio = 1.25;

    // mpv sizes its buffer to the size it was given; a pixel either way is rounding, not a pending resize.
    private const int Tolerance = 1;

    private PixelSize _panel;
    private long _hiddenSince;
    private uint? _presentsAtNewSize;
    private long _newSizeSince;

    /// <summary>The picture is hidden until mpv has presented at the panel's size.</summary>
    public bool IsHidden { get; private set; }

    /// <summary>The panel now measures <paramref name="panel"/> while the swap chain still holds <paramref name="buffer"/>.</summary>
    public void PanelResized(PixelSize panel, PixelSize buffer)
    {
        _panel = panel;
        _presentsAtNewSize = null;
        if (!IsHidden && !buffer.IsEmpty && !panel.IsEmpty && IsJump(buffer, panel))
        {
            IsHidden = true;
            _hiddenSince = time.GetTimestamp();
        }
    }

    /// <summary>
    /// A frame, with the buffer's size and the swap chain's present count now. Returns true when the picture shows:
    /// once mpv has presented again after the buffer took the panel's size, once it has presented nothing for
    /// <see cref="SettleTime"/> since, or after <see cref="LongestHide"/>.
    /// </summary>
    public bool Frame(PixelSize buffer, uint presents)
    {
        if (!IsHidden)
        {
            return true;
        }
        if (!Matches(buffer, _panel))
        {
            _presentsAtNewSize = null;
        }
        else if (_presentsAtNewSize is null)
        {
            _presentsAtNewSize = presents;
            _newSizeSince = time.GetTimestamp();
        }
        bool presentedAtNewSize = presents > _presentsAtNewSize;
        bool settled = _presentsAtNewSize is not null && time.GetElapsedTime(_newSizeSince) >= SettleTime;
        if (presentedAtNewSize || settled || time.GetElapsedTime(_hiddenSince) >= LongestHide)
        {
            IsHidden = false;
            return true;
        }
        return false;
    }

    /// <summary>The picture shows and nothing is awaited: the panel let go of the swap chain or got another output.</summary>
    public void Reset()
    {
        IsHidden = false;
        _presentsAtNewSize = null;
    }

    private static bool IsJump(PixelSize from, PixelSize to) =>
        Ratio(from.Width, to.Width) >= JumpRatio || Ratio(from.Height, to.Height) >= JumpRatio;

    private static double Ratio(int a, int b) => (double)Math.Max(a, b) / Math.Min(a, b);

    private static bool Matches(PixelSize buffer, PixelSize panel) =>
        Math.Abs(buffer.Width - panel.Width) <= Tolerance && Math.Abs(buffer.Height - panel.Height) <= Tolerance;
}
