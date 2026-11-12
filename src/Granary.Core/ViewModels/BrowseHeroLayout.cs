namespace Granary.Core.ViewModels;

/// <summary>
/// The sizes of the hero of a page of rows (Home, For You, Requests) and how it folds as the rows scroll (docs/ui-design.md,
/// Home). A page up to <see cref="ReferencePageHeight"/> high has the hero's own sizes; a taller one a taller hero, which
/// grows with the window (<see cref="For"/>). Set to <see cref="BrowseHeroSize.Full"/> (Settings → Navigation &amp; Cards →
/// Hero size) the hero is as tall as the banner of Silo's web, with the rows under it and its text at its foot. The fold
/// runs on the compositor as expressions over the scroll's translation; their numbers are scalars of a property set
/// (<c>p</c>, <see cref="ParameterNames"/>), so no number is ever written into the expression text in the user's culture
/// (a German "0,4375" would not parse), and a page of another height only sets them anew.
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

    /// <summary>How much lower the rows begin under Home's banner on such a page, for its Play and More Info.</summary>
    public const double BannerRoom = 48;

    /// <summary>
    /// How far the compact hero's shade reaches, in the text's own reach from the hero's left edge: the text hangs from the
    /// top, clear of the bottom fade the full hero's text stands in, so the side shade alone keeps it readable. It holds
    /// the full hero's 88 % to 58 % of the page's colour over the text and is clear three quarters of the text's reach
    /// further, which is as gentle a fall as the full hero's on a 1080 px window (<see cref="TextShadeWidth"/>).
    /// </summary>
    public const double TextShadeReach = 1.75;

    /// <summary>The logo's (or name's) height at rest on such a page; 45 px compact.</summary>
    public const double HeadingHeight = 80;

    /// <summary>The bottom fade's share of the backdrop's height.</summary>
    public const double FadeShare = 0.55;

    /// <summary>
    /// The full hero's bottom fade's share of its height: the web's <c>.hero-gradient</c> (its <c>app.css</c>) is clear above
    /// 66 % from the bottom.
    /// </summary>
    public const double FullFadeShare = 0.66;

    /// <summary>
    /// Which part of a backdrop taller than its box shows: a fifth of what is cut off goes at the top, the rest at the bottom,
    /// as the web's heroes place theirs (<c>object-[center_20%]</c> in its <c>components/HeroBanner.tsx</c>). Faces sit in the
    /// upper half of most backdrops; cut around the middle, the box took their heads.
    /// </summary>
    public const double BackdropFocus = 0.2;

    /// <summary>
    /// The full hero's share of the page's height, and its least and its most: the banner of Silo's web
    /// (<c>HERO_BANNER_SIZE</c> in its <c>lib/design-system.ts</c>, "h-[54vh] min-h-[380px] max-h-[760px] lg:h-[66vh]").
    /// Granary takes the wide layout's 66 % at every width, a desktop window being wide, and of the page's height, which
    /// is the window's without the title bar, where the web takes the browser window's.
    /// </summary>
    public const double FullShare = 0.66;

    /// <inheritdoc cref="FullShare"/>
    public const double FullMinHeight = 380;

    /// <inheritdoc cref="FullShare"/>
    public const double FullMaxHeight = 760;

    /// <summary>
    /// How far above the full hero's bottom its text ends, or under Home's banner its buttons: the padding under the text of
    /// the web's <c>components/HeroBanner.tsx</c> on a wide window (<c>lg:pb-16</c>).
    /// </summary>
    public const double FullTextBottom = 64;

    /// <summary>How far above the first row the buttons of Home's banner end under the compact hero.</summary>
    public const double BannerButtonsBottom = 26;

    /// <summary>The height of the banner's Play and More Info, and the room the web leaves between its text and them (<c>mb-7</c>).</summary>
    public const double BannerButtonHeight = 44;

    /// <inheritdoc cref="BannerButtonHeight"/>
    public const double BannerButtonsGap = 28;

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
        // The full hero's text goes up with the rows themselves, until it is where the header has it.
        ("Texts.Translation.Y", "Clamp(scroll.Translation.Y, -p.lift, 0)"),
        ("Heading.Scale", $"Vector3(1 - p.shrink * {Progress}, 1 - p.shrink * {Progress}, 1)"),
        ("Text.Translation.Y", $"-p.rise * {Progress}"),
        ("Overview.Opacity", $"Clamp(1 - 2 * {Progress}, 0, 1)"),
        ("Backdrop.Clip.BottomInset", $"p.cut * {Progress}"),
        ("Fade.Translation.Y", $"-p.fadeRise * {Progress}"),
        ("Fade.Scale", $"Vector3(1, 1 - p.fadeShrink * {Progress}, 1)"),
    ];

    /// <summary>The names of the fold's numbers (<see cref="ParametersFor"/>), which the expressions read from <c>p</c>.</summary>
    public static IReadOnlyList<string> ParameterNames { get; } = ["distance", "shrink", "rise", "cut", "fadeRise", "fadeShrink", "lift"];

    /// <summary>
    /// The hero's sizes on a page <paramref name="pageHeight"/> high, in <see cref="HeightStep"/> steps: the hero keeps its
    /// share of a taller page up to <see cref="MaxRestHeight"/>, and the first row moves down with it; the compact header and
    /// the logo grow half as much, and the text a quarter, so a tall window's hero is larger without its words turning big.
    /// A page of no known height has the hero's own sizes. Home's <paramref name="banner"/> begins the rows
    /// <see cref="BannerRoom"/> lower.
    /// <para>
    /// A <see cref="BrowseHeroSize.Full"/> hero takes <see cref="FullShare"/> of the page instead, between
    /// <see cref="FullMinHeight"/> and <see cref="FullMaxHeight"/> (its least on a page of no known height), and the rows begin
    /// under it. Its text ends <see cref="FullTextBottom"/> above its bottom, as the web's banner's; under Home's banner the
    /// buttons end there, with the text over them, and the hero is no taller. Its compact header, logo and type are the
    /// compact hero's on the same page, so it folds into the same header over its longer way.
    /// </para>
    /// </summary>
    public static BrowseHeroSizes For(double pageHeight, bool banner = false, BrowseHeroSize size = BrowseHeroSize.Compact)
    {
        double stepped = Math.Floor(pageHeight / HeightStep) * HeightStep;
        double scale = double.IsNaN(stepped) ? 1 : Math.Clamp(stepped / ReferencePageHeight, 1, MaxRestHeight / RestHeight);
        double grow = 1 + (scale - 1) / 2;
        double rest = Round(RestHeight * scale);
        double compact = Round(CompactHeight * grow);
        double rowsTop = Round((CompactHeight + CollapseDistance + (banner ? BannerRoom : 0)) * scale);
        var sizes = new BrowseHeroSizes(rest, compact, rowsTop, Round(HeadingHeight * grow), Round(CompactHeadingHeight * grow),
            Round(rest * FadeShare), grow, 1 + (scale - 1) / 4);
        if (size != BrowseHeroSize.Full)
        {
            return sizes;
        }
        double full = double.IsNaN(stepped) ? FullMinHeight : Round(Math.Clamp(stepped * FullShare, FullMinHeight, FullMaxHeight));
        return sizes with
        {
            RestHeight = full,
            RowsTop = full,
            FadeHeight = Round(full * FullFadeShare),
            TextBottom = FullTextBottom + (banner ? BannerButtonHeight + BannerButtonsGap : 0),
            ButtonsBottom = FullTextBottom,
        };
    }

    /// <summary>
    /// Whether the rows scrolled <paramref name="offset"/> fold the hero: past half the way, where a new backdrop shows only its
    /// blur and Home's banner holds still, as the web's scrolled out of view.
    /// </summary>
    public static bool IsFolded(double offset, BrowseHeroSizes sizes)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        return offset > sizes.CollapseDistance / 2;
    }

    /// <summary><paramref name="size"/> as it is at <see cref="ReferencePageHeight"/>, grown by <paramref name="grow"/> to whole pixels.</summary>
    public static double Scaled(double size, double grow) => Round(size * grow);

    /// <summary>
    /// The backdrop box's width on a page <paramref name="pageWidth"/> wide: the hero's whole width at either size, as the
    /// web's banner's. The compact hero is lower, so it shows less of the picture's height than the full one.
    /// </summary>
    public static double BackdropBoxWidth(double pageWidth) => Round(pageWidth);

    /// <summary>
    /// How wide the compact hero's shade is, from its left edge, over text that ends <paramref name="textRight"/> from there
    /// (<see cref="TextShadeReach"/>); none while that is not known.
    /// </summary>
    public static double TextShadeWidth(double textRight) =>
        textRight > 0 && double.IsFinite(textRight) ? Round(textRight * TextShadeReach) : 0;

    /// <summary>
    /// Where a backdrop lies in a box <paramref name="boxWidth"/> by <paramref name="boxHeight"/> it fills: as wide as the box,
    /// or as wide as the box's height needs at the picture's shape, its sides cut evenly and its height by
    /// <see cref="BackdropFocus"/>. In whole pixels, from the box's top left; nothing while the box has no size.
    /// </summary>
    public static BackdropPlace PlaceBackdrop(double boxWidth, double boxHeight)
    {
        if (!(boxWidth > 0 && boxHeight > 0) || !double.IsFinite(boxWidth) || !double.IsFinite(boxHeight))
        {
            return default;
        }
        // 16 and 9 apart, so a box of the picture's own shape gets it whole, with no pixel more from rounding.
        double width = Math.Ceiling(Math.Max(boxWidth, boxHeight * 16 / 9));
        double height = Math.Ceiling(Math.Max(boxHeight, width * 9 / 16));
        return new BackdropPlace(width, height, Round((boxWidth - width) / 2), Round((boxHeight - height) * BackdropFocus));
    }

    /// <summary>
    /// How wide the hero draws its backdrop in a window <paramref name="windowWidth"/> by <paramref name="windowHeight"/>, for
    /// asking the server for a picture that wide (the web's <c>heroBackdropWidth</c> in its <c>lib/artworkWidths.ts</c>). The
    /// window's size stands in for the page's, which is not laid out when its rows are asked for: a little too wide, never
    /// too narrow. 0 while the width is not known.
    /// </summary>
    public static double BackdropDrawnWidth(double windowWidth, double windowHeight, BrowseHeroSize size)
    {
        if (!(windowWidth > 0) || !double.IsFinite(windowWidth))
        {
            return 0;
        }
        var sizes = For(windowHeight, banner: false, size);
        return BackdropDecodeWidth(BackdropBoxWidth(windowWidth), sizes.RestHeight);
    }

    /// <summary>
    /// How far a full hero's text goes up as the rows scroll, from its place at the hero's foot to the compact header's:
    /// <paramref name="textHeight"/> is the text's height now, which changes with the title, and <paramref name="headerTop"/>
    /// where the header's text begins. None for the compact hero, whose text is there already, nor for text too tall for a
    /// small hero.
    /// </summary>
    public static double TextLift(BrowseHeroSizes sizes, double textHeight, double headerTop)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        return sizes.TextBottom is { } bottom ? Math.Max(0, sizes.RestHeight - bottom - textHeight - headerTop) : 0;
    }

    /// <summary>
    /// The fold's numbers (<see cref="ParameterNames"/>) for a hero of <paramref name="sizes"/>, whose text goes up
    /// <paramref name="lift"/> (<see cref="TextLift"/>).
    /// </summary>
    public static IReadOnlyDictionary<string, float> ParametersFor(BrowseHeroSizes sizes, double lift = 0)
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
            ["lift"] = (float)lift,
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
    /// the width comes: waiting for the fade there left it blurred. A hidden episode still (<paramref name="blurred"/>) is
    /// never made again: its blur is at most 128 px stretched over the box, and would only fade over itself.
    /// </summary>
    public static BackdropStep StepFor(bool waitsForArt, bool folded, bool fading, bool blurred, int decoded, int needed) =>
        needed <= 0 ? BackdropStep.None
        : waitsForArt ? (folded ? BackdropStep.None : BackdropStep.LoadArt)
        : !fading && !blurred && DecodesAgain(decoded, needed) ? BackdropStep.DecodeAgain
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
/// <param name="TextBottom">
/// How far above the hero's bottom its text ends at rest (<see cref="BrowseHeroSize.Full"/>); null where the text hangs from
/// the top, as the compact hero's.
/// </param>
/// <param name="ButtonsBottom">How far above the first row the buttons of Home's banner end.</param>
public sealed record BrowseHeroSizes(double RestHeight, double CompactHeight, double RowsTop, double HeadingHeight, double CompactHeadingHeight,
    double FadeHeight, double Grow, double TextGrow, double? TextBottom = null, double ButtonsBottom = BrowseHeroLayout.BannerButtonsBottom)
{
    /// <summary>How far the rows scroll before the hero is compact.</summary>
    public double CollapseDistance => RowsTop - CompactHeight;

    /// <summary>The hero is the full one (<see cref="BrowseHeroSize.Full"/>): its text stands at its foot, under the fades of the web's banner.</summary>
    public bool IsFull => TextBottom is not null;
}

/// <summary>A backdrop's size and where its top left lies from its box's (<see cref="BrowseHeroLayout.PlaceBackdrop"/>).</summary>
public readonly record struct BackdropPlace(double Width, double Height, double Left, double Top);

/// <summary>
/// Settings → Navigation &amp; Cards → Hero size: how tall the hero at the top of Home, For You and Requests' Discover is,
/// Home's banner included (<see cref="BrowseHeroLayout.For"/>).
/// </summary>
public enum BrowseHeroSize
{
    /// <summary>Granary's own sizes: the first row shows without scrolling.</summary>
    Compact,

    /// <summary>As tall as the banner of Silo's web, with the rows under it.</summary>
    Full,
}

/// <summary>What <see cref="BrowseHeroLayout.StepFor"/> asks of the backdrop shown.</summary>
public enum BackdropStep
{
    None,
    LoadArt,
    DecodeAgain,
}
