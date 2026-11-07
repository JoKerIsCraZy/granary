using System.Numerics;
using Granary.Core.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Granary.App.Controls;

/// <summary>
/// The buttons of Home's banner (<see cref="Banner"/>), over the room between the hero's compact header and the first row
/// (<see cref="Sizes"/>). The rail starts empty with each slide and countdown (<see cref="Cycle"/>), fills on the compositor
/// while a countdown runs and stops while none does (<see cref="IsAdvancing"/>); with Windows' animations off it stands full,
/// as the web's under reduced motion. The arrows show while the pointer or focus is on the banner (<see cref="IsEngaged"/>),
/// as the web's on a wide window.
/// </summary>
public sealed partial class FeaturedBannerControls : UserControl
{
    public static readonly DependencyProperty BannerProperty =
        DependencyProperty.Register(nameof(Banner), typeof(FeaturedBannerViewModel), typeof(FeaturedBannerControls), new PropertyMetadata(null));

    public static readonly DependencyProperty SizesProperty =
        DependencyProperty.Register(nameof(Sizes), typeof(BrowseHeroSizes), typeof(FeaturedBannerControls),
            new PropertyMetadata(BrowseHeroLayout.For(0, banner: true), (sender, _) => ((FeaturedBannerControls)sender).FollowSizes()));

    public static readonly DependencyProperty CycleProperty =
        DependencyProperty.Register(nameof(Cycle), typeof(int), typeof(FeaturedBannerControls),
            new PropertyMetadata(0, (sender, _) => ((FeaturedBannerControls)sender).FollowRail()));

    public static readonly DependencyProperty IsAdvancingProperty =
        DependencyProperty.Register(nameof(IsAdvancing), typeof(bool), typeof(FeaturedBannerControls),
            new PropertyMetadata(false, (sender, _) => ((FeaturedBannerControls)sender).FollowRail()));

    private const double ArrowSize = 36;
    private const double ArrowsShown = 0.9;
    private static readonly TimeSpan ArrowFade = TimeSpan.FromMilliseconds(150);

    private static readonly Vector3 Empty = new(0, 1, 1);

    private readonly bool _animates = new UISettings().AnimationsEnabled;
    private readonly Visual _railFill;
    private Vector3KeyFrameAnimation? _filling;
    private AnimationController? _rail;
    // The slide or countdown the rail is for; a new one starts it empty again.
    private int _railCycle = -1;

    public FeaturedBannerControls()
    {
        InitializeComponent();
        PreviousButton.OpacityTransition = new ScalarTransition { Duration = ArrowFade };
        NextButton.OpacityTransition = new ScalarTransition { Duration = ArrowFade };
        // The rail fills from the left; without animations it stands full.
        _railFill = ElementCompositionPreview.GetElementVisual(RailFill);
        _railFill.Scale = _animates ? Empty : Vector3.One;
        FollowSizes();
    }

    public FeaturedBannerViewModel? Banner
    {
        get => (FeaturedBannerViewModel?)GetValue(BannerProperty);
        set => SetValue(BannerProperty, value);
    }

    /// <summary>The hero's sizes, which give the room's height and where the banner's middle is.</summary>
    public BrowseHeroSizes Sizes
    {
        get => (BrowseHeroSizes)GetValue(SizesProperty);
        set => SetValue(SizesProperty, value);
    }

    /// <summary>The banner's <see cref="FeaturedBannerViewModel.Cycle"/>: each new slide or countdown starts the rail empty.</summary>
    public int Cycle
    {
        get => (int)GetValue(CycleProperty);
        set => SetValue(CycleProperty, value);
    }

    /// <summary>The banner's <see cref="FeaturedBannerViewModel.IsAdvancing"/>: the rail stops where it is while it holds.</summary>
    public bool IsAdvancing
    {
        get => (bool)GetValue(IsAdvancingProperty);
        set => SetValue(IsAdvancingProperty, value);
    }

    /// <summary>The pointer or focus is on the banner: the arrows show.</summary>
    public bool IsEngaged
    {
        get;
        set
        {
            field = value;
            PreviousButton.Opacity = NextButton.Opacity = value ? ArrowsShown : 0;
        }
    }

    /// <summary>Whether <paramref name="element"/> is one of these buttons, or inside one.</summary>
    public bool Holds(DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, this))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A remote's Play with focus on these buttons plays the slide shown, as on a card.</summary>
    public bool PlayFromKey(DependencyObject focused)
    {
        if (Banner is not { CanPlay: true } banner || !Holds(focused))
        {
            return false;
        }
        banner.PlayCommand.Execute(null);
        return true;
    }

    // Instance method because x:Bind function bindings call it on the control.
    public string PauseGlyph(bool isPaused) => isPaused ? "" : "";

    // The room reaches from the compact header to the first row; the arrows sit half way down the whole banner, which
    // begins at the top of the page.
    private void FollowSizes()
    {
        var sizes = Sizes;
        Height = sizes.CollapseDistance;
        double top = Math.Max(0, sizes.RowsTop / 2 - sizes.CompactHeight - ArrowSize / 2);
        PreviousButton.Margin = PreviousButton.Margin with { Top = top };
        NextButton.Margin = NextButton.Margin with { Top = top };
    }

    // A new countdown fills the rail from empty over the slide's 8 s, as the web restarts its rail with each; a new slide that
    // waits (held, or paused) shows it empty, and a countdown stopped leaves it where it got to. The two numbers come one
    // after the other: a countdown told before its number fills an empty rail at once.
    private void FollowRail()
    {
        if (!_animates)
        {
            return;
        }
        if (Cycle == _railCycle)
        {
            if (!IsAdvancing)
            {
                _rail?.Pause();
            }
            else if (_rail is null)
            {
                Fill();
            }
            return;
        }
        _railCycle = Cycle;
        if (IsAdvancing)
        {
            Fill();
            return;
        }
        _railFill.StopAnimation("Scale");
        _rail = null;
        _railFill.Scale = Empty;
    }

    private void Fill()
    {
        _railFill.StartAnimation("Scale", _filling ??= Filling(_railFill.Compositor));
        _rail = _railFill.TryGetAnimationController("Scale");
    }

    // The rail's fill over a slide's time, made once and started again for each countdown.
    private static Vector3KeyFrameAnimation Filling(Compositor compositor)
    {
        var linear = compositor.CreateLinearEasingFunction();
        var fill = compositor.CreateVector3KeyFrameAnimation();
        fill.InsertKeyFrame(0, Empty, linear);
        fill.InsertKeyFrame(1, Vector3.One, linear);
        fill.Duration = FeaturedBannerViewModel.AdvanceTime;
        return fill;
    }
}
