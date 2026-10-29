namespace Granary.Core.ViewModels;

/// <summary>
/// The sizes of the hero of a page of rows (Home, For You) and how it folds as the rows scroll (docs/ui-design.md, Home). The fold runs on the compositor as
/// expressions over the scroll's translation; their numbers are named parameters, so no number is ever written into the
/// expression text in the user's culture (a German "0,4375" would not parse).
/// </summary>
public static class BrowseHeroLayout
{
    /// <summary>The hero's height at rest; its backdrop reaches down behind the first row.</summary>
    public const double RestHeight = 440;

    /// <summary>The compact header's height, where the rows are cut off once they scroll.</summary>
    public const double CompactHeight = 150;

    /// <summary>How far the rows scroll before the hero is compact: they begin this far below the compact header.</summary>
    public const double CollapseDistance = 150;

    /// <summary>The backdrop's share of the hero's width, from the right.</summary>
    public const double BackdropShare = 0.65;

    /// <summary>The logo's (or name's) height at rest; 45 px compact.</summary>
    public const double HeadingHeight = 80;

    /// <summary>The bottom fade's share of the backdrop's height.</summary>
    public const double FadeShare = 0.55;

    private const double CompactHeadingHeight = 45;
    // A backdrop's own shape, for how much of it the box cuts off.
    private const double BackdropAspect = 16.0 / 9;
    // How much wider a backdrop must be shown than it was decoded before it is decoded again.
    private const double RedecodeGrowth = 1.15;

    /// <summary>The fold's progress, 0 at rest and 1 compact, from the scroll's translation (<c>scroll</c>).</summary>
    public const string Progress = "Clamp(-scroll.Translation.Y / distance, 0, 1)";

    /// <summary>What each part of the hero does as the fold progresses, by name.</summary>
    public static IReadOnlyList<(string Target, string Text)> Expressions { get; } =
    [
        ("Heading.Scale", $"Vector3(1 - shrink * {Progress}, 1 - shrink * {Progress}, 1)"),
        ("Text.Translation.Y", $"-rise * {Progress}"),
        ("Overview.Opacity", $"Clamp(1 - 2 * {Progress}, 0, 1)"),
        ("Backdrop.Clip.BottomInset", $"cut * {Progress}"),
        ("Fade.Translation.Y", $"-fadeRise * {Progress}"),
        ("Fade.Scale", $"Vector3(1, 1 - fadeShrink * {Progress}, 1)"),
    ];

    /// <summary>The named numbers the expressions use.</summary>
    public static IReadOnlyDictionary<string, float> Parameters { get; } = new Dictionary<string, float>(StringComparer.Ordinal)
    {
        ["distance"] = (float)CollapseDistance,
        ["shrink"] = (float)(1 - CompactHeadingHeight / HeadingHeight),
        ["rise"] = (float)(HeadingHeight - CompactHeadingHeight),
        ["cut"] = (float)(RestHeight - CompactHeight),
        // Its end a little past the cut, so no line of the picture shows under it.
        ["fadeRise"] = (float)(RestHeight - CompactHeight - 2),
        ["fadeShrink"] = (float)(1 - CompactHeight * FadeShare / (RestHeight * FadeShare)),
    };

    /// <summary>
    /// Where a wheel notch over the hero scrolls the rows: on from where the last notch was going, so quick notches add up,
    /// and never past either end.
    /// </summary>
    public static double WheelTarget(double offset, double? pending, int delta, double scrollable) =>
        Math.Clamp((pending ?? offset) - delta, 0, Math.Max(0, scrollable));

    /// <summary>
    /// How wide to decode a backdrop shown <paramref name="boxWidth"/> wide and <see cref="RestHeight"/> high: as wide as
    /// shown, or wider when the box's shape cuts its sides; 0 while the width is not known.
    /// </summary>
    public static int BackdropDecodeWidth(double boxWidth) => BackdropDecodeWidth(boxWidth, RestHeight);

    /// <summary>
    /// How wide to decode a backdrop filling a box <paramref name="boxWidth"/> by <paramref name="boxHeight"/> (Search's
    /// backdrop): as wide as the box, or as wide as the box's height needs at the picture's shape; 0 while the width is not
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

/// <summary>What <see cref="BrowseHeroLayout.StepFor"/> asks of the backdrop shown.</summary>
public enum BackdropStep
{
    None,
    LoadArt,
    DecodeAgain,
}
