namespace Granary.Core.ViewModels.Player;

/// <summary>
/// The mouse cursor over the player's picture (PlayerPage.AutoHide.cs): it follows the mouse only. A real move shows it,
/// and it hides with the controls while it is over the picture, whatever hid them: the idle wait, a remote's Back, a tap.
/// Keys and the player's own changes show the controls and leave the cursor as it is, so a remote never brings it back
/// over the picture. Hidden, it keeps nothing up: one resting on the bar would otherwise hold the controls for good.
/// </summary>
public sealed class PlayerCursor
{
    /// <summary>Moves this small are jitter, or the framework testing a still pointer again as the controls change.</summary>
    public const double MoveThreshold = 2;

    // Where the last real move ended: small moves count once they add up from there.
    private (double X, double Y)? _last;

    public bool IsHidden { get; private set; }

    /// <summary>The pointer is at <paramref name="x"/>, <paramref name="y"/> in the page; true for a real move, which shows the cursor.</summary>
    public bool Moves(double x, double y)
    {
        if (_last is { } last && Math.Abs(x - last.X) < MoveThreshold && Math.Abs(y - last.Y) < MoveThreshold)
        {
            return false;
        }
        _last = (x, y);
        IsHidden = false;
        return true;
    }

    /// <summary>The controls hid; a cursor over the picture goes with them.</summary>
    public void ControlsHidden(bool isOverPicture) => IsHidden |= isOverPicture;

    /// <summary>
    /// The player is left: the cursor shows for the pages under it, and the next move counts wherever it starts, should the
    /// page load again.
    /// </summary>
    public void Show()
    {
        IsHidden = false;
        _last = null;
    }

    /// <summary>Whether the pointer over the controls keeps them up: only while the cursor shows.</summary>
    public bool HoldsControls(bool isOverControls) => isOverControls && !IsHidden;
}
