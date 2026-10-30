namespace Granary.Core.ViewModels.Detail;

/// <summary>
/// The trailer's 16:9 frame: as wide as the web's TrailerModal at most (<c>max-w-4xl</c>), and within the room the window
/// leaves it, in both directions.
/// </summary>
public static class TrailerFrame
{
    public const double MaximumWidth = 896;

    /// <summary>The largest 16:9 frame in <paramref name="width"/> by <paramref name="height"/>, no wider than <see cref="MaximumWidth"/>.</summary>
    public static (double Width, double Height) Fit(double width, double height)
    {
        double fitted = Math.Max(0, Math.Min(Math.Min(MaximumWidth, width), height * 16 / 9));
        return (fitted, fitted * 9 / 16);
    }
}
