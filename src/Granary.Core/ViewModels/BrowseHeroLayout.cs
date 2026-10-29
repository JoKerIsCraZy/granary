namespace Granary.Core.ViewModels;

/// <summary>
/// The sizes of the hero of a page of rows (Home, For You, Requests) and how it folds as the rows scroll (docs/ui-design.md,
/// Home). A page up to <see cref="ReferencePageHeight"/> high has the hero's own sizes; a taller one a taller hero, as
/// Silage's grows with the window (<see cref="For"/>). The fold runs on the compositor as expressions over the scroll's
/// translation; their numbers are scalars of a property set (<c>p</c>, <see cref="ParameterNames"/>), so no number is ever
/// written into the expression text in the user's culture (a German "0,4375" would not parse), and a page of another
/// height only sets them anew.
/// </summary>
public static class BrowseHeroLayout
{
    /// <summary>The page height the hero's own sizes are for: a page under a 1080 px window, its title bar and the taskbar off.</summary>
    public const double ReferencePageHeight = 1040;

    /// <summary>The hero's height at rest on such a page; its backdrop reaches down behind the first row.</summary>
    public const double RestHeight = 440;

    /// <summary>The hero's height at most, however tall the page.</summary>
    public const double MaxRestHeight = 720;

    /// <summary>The page heights the sizes change at: a window resized by a few pixels keeps them, and its text and rows stay put.</summary>
    public const double HeightStep = 8;

    /// <summary>The compact header's height on such a page, where the rows are cut off once they scroll.</summary>
    public const double CompactHeight = 150;

    /// <summary>How far the rows scroll before the hero is compact on such a page: they begin this far below the compact header.</summary>
    public const double CollapseDistance = 150;

    /// <summary>The backdrop's share of the hero's width, from the right.</summary>
    public const double BackdropShare = 0.65;

    /// <summary>
    /// The backdrop box's widest shape: on a wide window it is narrowed from the left, so a backdrop is not blown up into a
    /// strip (on a 1080 px window the box is just under it).
    /// </summary>
    public const double MaxBackdropAspect = 2.5;

    /// <summary>The logo's (or name's) height at rest on such a page; 45 px compact.</summary>
    public const double HeadingHeight = 80;

    /// <summary>The bottom fade's share of the backdrop's height.</summary>
    public const double FadeShare = 0.55;

    private const double CompactHeadingHeight = 45;
    // A backdrop's own shape, for how much of it the box cuts off.
    private const double BackdropAspect = 16.0 / 9;
    // How much wider a backdrop must be shown than it was decoded before it is decoded again.
    private const double RedecodeGrowth = 1.15;

    /// <summary>The fold's progress, 0 at rest and 1 compact, from the scroll's translation (<c>scroll</c>).</summary>
    public const string Progress = "Clamp(-scroll.Translation.Y / p.distance, 0, 1)";

    /// <summary>What each part of the hero does as the fold progresses, by name.</summary>
    public static IReadOnlyList<(string Target, string Text)> Expressions { get; } =
    [
        ("Heading.Scale", $"Vector3(1 - p.shrink * {Progress}, 1 - p.shrink * {Progress}, 1)"),
        ("Text.Translation.Y", $"-p.rise * {Progress}"),
        ("Overview.Opacity", $"Clamp(1 - 2 * {Progress}, 0, 1)"),
        ("Backdrop.Clip.BottomInset", $"p.cut * {Progress}"),
        ("Fade.Translation.Y", $"-p.fadeRise * {Progress}"),
        ("Fade.Scale", $"Vector3(1, 1 - p.fadeShrink * {Progress}, 1)"),
    ];

    /// <summary>The names of the fold's numbers (<see cref="ParametersFor"/>), which the expressions read from <c>p</c>.</summary>
    public static IReadOnlyList<string> ParameterNames { get; } = ["distance", "shrink", "rise", "cut", "fadeRise", "fadeShrink"];

    /// <summary>
    /// The hero's sizes on a page <paramref name="pageHeight"/> high, in <see cref="HeightStep"/> steps: the hero keeps its
    /// share of a taller page up to <see cref="MaxRestHeight"/>, and the first row moves down with it; the compact header and
    /// the logo grow half as much, and the text a quarter, so a tall window's hero is larger without its words turning big.
    /// A page of no known height has the hero's own sizes.
    /// </summary>
    public static BrowseHeroSizes For(double pageHeight)
    {
        double stepped = Math.Floor(pageHeight / HeightStep) * HeightStep;
        double scale = double.IsNaN(stepped) ? 1 : Math.Clamp(stepped / ReferencePageHeight, 1, MaxRestHeight / RestHeight);
        double grow = 1 + (scale - 1) / 2;
        double rest = Round(RestHeight * scale);
        double compact = Round(CompactHeight * grow);
        return new BrowseHeroSizes(rest, compact, Round((CompactHeight + CollapseDistance) * scale), Round(HeadingHeight * grow),
            Round(CompactHeadingHeight * grow), Round(rest * FadeShare), grow, 1 + (scale - 1) / 4);
    }

    /// <summary><paramref name="size"/> as it is at <see cref="ReferencePageHeight"/>, grown by <paramref name="grow"/> to whole pixels.</summary>
    public static double Scaled(double size, double grow) => Round(size * grow);

    /// <summary>
    /// The backdrop box's width on a page <paramref name="pageWidth"/> wide: its share, but never wider than
    /// <see cref="MaxBackdropAspect"/> times the hero's height.
    /// </summary>
    public static double BackdropBoxWidth(double pageWidth, BrowseHeroSizes sizes)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        return Round(Math.Min(pageWidth * BackdropShare, sizes.RestHeight * MaxBackdropAspect));
    }

    /// <summary>The fold's numbers (<see cref="ParameterNames"/>) for a hero of <paramref name="sizes"/>.</summary>
    public static IReadOnlyDictionary<string, float> ParametersFor(BrowseHeroSizes sizes)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        double cut = sizes.RestHeight - sizes.CompactHeight;
        return new Dictionary<string, float>(StringComparer.Ordinal)
        {
            ["distance"] = (float)sizes.CollapseDistance,
            ["shrink"] = (float)(1 - sizes.CompactHeadingHeight / sizes.HeadingHeight),
            ["rise"] = (float)(sizes.HeadingHeight - sizes.CompactHeadingHeight),
            ["cut"] = (float)cut,
            // Its end a little past the cut, so no line of the picture shows under it.
            ["fadeRise"] = (float)(cut - 2),
            ["fadeShrink"] = (float)(1 - sizes.CompactHeight / sizes.RestHeight),
        };
    }

    private static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Where a wheel notch over the hero scrolls the rows: on from where the last notch was going, so quick notches add up,
    /// and never past either end.
    /// </summary>
    public static double WheelTarget(double offset, double? pending, int delta, double scrollable) =>
        Math.Clamp((pending ?? offset) - delta, 0, Math.Max(0, scrollable));

    /// <summary>
    /// How wide to decode a backdrop filling a box <paramref name="boxWidth"/> by <paramref name="boxHeight"/> (the hero's,
    /// Search's): as wide as the box, or as wide as the box's height needs at the picture's shape; 0 while the width is not
    /// known.
    /// </summary>
    public static int BackdropDecodeWidth(double boxWidth, double boxHeight) => boxWidth > 0
        ? (int)Math.Ceiling(Math.Max(boxWidth, double.IsFinite(boxHeight) && boxHeight > 0 ? boxHeight * BackdropAspect : 0))
        : 0;

    /// <summary>A backdrop decoded <paramref name="decoded"/> wide is decoded again for <paramref name="needed"/> when it grew notably.</summary>
    public static bool DecodesAgain(int decoded, int needed) => needed > 0 && needed > decoded * RedecodeGrowth;

    /// <summary>
    /// What the backdrop shown does once its width is known (<paramref name="needed"/> to decode): a picture that waited
    /// loads, also while its fade runs, since it fades in over its blur; one decoded notably narrower fades in again wider
    /// once the fade is over. The first title of a start shows before the hero has a width, and its fade still runs when
    /// the width comes: waiting for the fade there left it blurred.
    /// </summary>
    public static BackdropStep StepFor(bool waitsForArt, bool folded, bool fading, int decoded, int needed) =>
        needed <= 0 ? BackdropStep.None
        : waitsForArt ? (folded ? BackdropStep.None : BackdropStep.LoadArt)
        : !fading && DecodesAgain(decoded, needed) ? BackdropStep.DecodeAgain
        : BackdropStep.None;
}

/// <summary>The hero's sizes on a page of one height (<see cref="BrowseHeroLayout.For"/>), in whole pixels.</summary>
/// <param name="RestHeight">The hero at rest, and its backdrop's height.</param>
/// <param name="CompactHeight">The compact header the rows scroll under.</param>
/// <param name="RowsTop">Where the first row begins at rest.</param>
/// <param name="HeadingHeight">The logo's (or name's) height at rest.</param>
/// <param name="CompactHeadingHeight">The logo's height in the compact header.</param>
/// <param name="FadeHeight">The backdrop's bottom fade.</param>
/// <param name="Grow">How much the logo and the text block grow (1 at <see cref="BrowseHeroLayout.ReferencePageHeight"/>).</param>
/// <param name="TextGrow">How much the details and the overview's type grow.</param>
public sealed record BrowseHeroSizes(double RestHeight, double CompactHeight, double RowsTop, double HeadingHeight, double CompactHeadingHeight,
    double FadeHeight, double Grow, double TextGrow)
{
    /// <summary>How far the rows scroll before the hero is compact.</summary>
    public double CollapseDistance => RowsTop - CompactHeight;
}

/// <summary>What <see cref="BrowseHeroLayout.StepFor"/> asks of the backdrop shown.</summary>
public enum BackdropStep
{
    None,
    LoadArt,
    DecodeAgain,
}
