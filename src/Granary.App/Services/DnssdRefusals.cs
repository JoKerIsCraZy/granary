namespace Granary.App.Services;

/// <summary>
/// Windows' refusals to take back one registration of <see cref="DnssdServiceAdvertiser"/>, and when it is given up. Apart
/// from Windows, so that tests reach it. A registration Windows did not take back stays the current one, so no second one
/// of the PC's name is made beside it; but Windows may never take it (an adapter gone with a network change, a request it
/// no longer knows, an answer that never comes), and kept for good it would leave the PC unadvertised while Granary runs.
/// So it is kept for a few tries or half a minute, then given up, and a new one is made: an overlap is then possible, for
/// as long as Granary runs at the latest. Microsoft's page for <c>DnsServiceDeRegister</c> names no particular codes;
/// Windows' general words for a request it does not know or will not take give it up at once.
/// </summary>
internal sealed class DnssdRefusals
{
    /// <summary>The refusal that gives a registration up.</summary>
    public const int Limit = 3;

    /// <summary>How long after the first refusal one more gives a registration up.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(30);

    // ERROR_NOT_FOUND and ERROR_INVALID_PARAMETER: asked again, Windows would answer the same.
    private const uint NotFound = 1168;
    private const uint InvalidParameter = 87;

    private int _count;
    private long _first;

    /// <summary>
    /// Counts a refusal with its <paramref name="status"/> at <paramref name="now"/> (milliseconds on one clock); true once
    /// the registration is given up.
    /// </summary>
    public bool GivesUp(uint status, long now)
    {
        if (_count++ == 0)
        {
            _first = now;
        }
        return status is NotFound or InvalidParameter || _count >= Limit || now - _first >= (long)GiveUpAfter.TotalMilliseconds;
    }
}
