namespace Granary.Core.ViewModels.Player;

/// <summary>
/// Whether a row's later parts show after its first: the minimized player's end time after its times (the App's
/// <c>FittingRow</c>). They show while the whole row fits; once left out, they come back only with room to spare, so a
/// time that grows by a digit at the edge ("-9:59" to "-10:00" and back) does not make them blink.
/// </summary>
public static class RowFit
{
    /// <summary>The room, in pixels, a row must have to spare before parts it left out show again: a digit and a bit.</summary>
    public const double ComebackMargin = 12;

    // Half a pixel of layout rounding is no reason to leave anything out.
    private const double Rounding = 0.5;

    /// <param name="rowWidth">The whole row's width, every part in it.</param>
    /// <param name="room">The width the row has.</param>
    /// <param name="shownBefore">The later parts showed at the last layout.</param>
    public static bool ShowsAll(double rowWidth, double room, bool shownBefore) =>
        shownBefore ? rowWidth <= room + Rounding : rowWidth + ComebackMargin <= room;
}
