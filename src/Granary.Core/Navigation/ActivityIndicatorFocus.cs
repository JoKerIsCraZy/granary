namespace Granary.Core.Navigation;

/// <summary>Where keyboard focus is when an arrow comes, as the activity indicator's rules see it.</summary>
public enum ActivityFocusPlace
{
    /// <summary>The sidebar, the indicator itself, a menu, or nowhere.</summary>
    Elsewhere,

    /// <summary>In the page.</summary>
    Page,

    /// <summary>On the way back, the disc at the page's top left.</summary>
    PageBack,
}

/// <summary>What the arrows would move to above the focused element.</summary>
public enum UpTarget
{
    Nothing,

    /// <summary>Another element of the page.</summary>
    Page,

    /// <summary>The way back, the disc at the page's top left.</summary>
    PageBack,

    /// <summary>Anything else outside the page: the sidebar, the indicator.</summary>
    Elsewhere,
}

public enum ActivityStep
{
    /// <summary>The key is the arrows' own: focus moves as it would without an indicator.</summary>
    LeaveToArrows,

    /// <summary>The key is taken and focus stays: a held key stops at the page's edge.</summary>
    Stay,

    /// <summary>Focus moves to the indicator's button.</summary>
    FocusIndicator,
}

/// <summary>
/// The keyboard's and a TV remote's way to the administrator's activity indicator (MainWindow.Activity.cs), which floats
/// over the page's top right corner, outside the page, where the arrows do not reach by themselves.
/// </summary>
public static class ActivityIndicatorFocus
{
    /// <summary>
    /// What Up does. At the page's top edge it goes to the indicator, as Left at its left edge goes to the sidebar, and
    /// crossing takes a new press. The way back lies at the page's top too: when the arrows would go to it, they do, as
    /// before there was an indicator, and Up from it goes on to the indicator, since nothing lies above it.
    /// </summary>
    public static ActivityStep Up(bool showsIndicator, ActivityFocusPlace from, UpTarget above, bool isRepeat)
    {
        if (!showsIndicator || from == ActivityFocusPlace.Elsewhere)
        {
            return ActivityStep.LeaveToArrows;
        }
        if (from == ActivityFocusPlace.Page && above is UpTarget.Page or UpTarget.PageBack)
        {
            return ActivityStep.LeaveToArrows;
        }
        return isRepeat ? ActivityStep.Stay : ActivityStep.FocusIndicator;
    }

    /// <summary>
    /// Whether an indicator that is about to go (its last activity ended, or its panel closed on a page where it shows
    /// only while open) gives keyboard focus back to the page first: when it holds it, and the app shows. Under the full
    /// player focus is the player's.
    /// </summary>
    public static bool GivesFocusBack(bool holdsFocus, bool isAppShown) => holdsFocus && isAppShown;
}

/// <summary>Where the activity indicator lies over a page, for pages whose own controls reach into its corner.</summary>
public static class ActivityIndicatorPlace
{
    /// <summary>The button's distance from the page's right edge, as on the web.</summary>
    public const double EdgeMargin = 20;

    /// <summary>The button's width.</summary>
    public const double Size = 36;

    /// <summary>The room kept between the button and a control beside it.</summary>
    public const double Gap = 8;

    /// <summary>How far in from the page's right edge the button's room reaches.</summary>
    public const double Room = EdgeMargin + Size + Gap;

    /// <summary>
    /// How far left a control that ends at <paramref name="right"/> must move to stay clear of the indicator on a page
    /// <paramref name="pageWidth"/> wide; 0 when it ends before the indicator's room.
    /// </summary>
    public static double InsetFor(double pageWidth, double right) =>
        double.IsFinite(pageWidth) && double.IsFinite(right) && right > 0 ? Math.Max(0, right - (pageWidth - Room)) : 0;
}
