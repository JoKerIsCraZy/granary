using System.Numerics;
using Granary.Core.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Controls;

/// <summary>
/// The top of a page of rows (Home, For You): <see cref="Title"/>'s logo or name, its details and overview, over its
/// backdrop, as Silage's home shows the card in focus. A new title's backdrop fades in over the last one, its blur first;
/// the last one stays fully shown underneath until the fade is done. Once the rows below scroll, the hero folds into a
/// compact header of the logo and the details, which stays at the top (<see cref="FollowScroll"/>); folded, a new title
/// shows only its backdrop's blur. On a taller page the hero is taller (<see cref="Sizes"/>, <see cref="BrowseHeroLayout.For"/>).
/// The page lays its rows under it with <see cref="RowsViewMargin"/> and <see cref="RowsPadding"/>.
/// </summary>
public sealed partial class BrowseHero : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(BrowseHeroTitle), typeof(BrowseHero),
            new PropertyMetadata(null, (sender, e) => ((BrowseHero)sender).Show(e.NewValue as BrowseHeroTitle)));

    public static readonly DependencyProperty SizesProperty =
        DependencyProperty.Register(nameof(Sizes), typeof(BrowseHeroSizes), typeof(BrowseHero), new PropertyMetadata(BrowseHeroLayout.For(0)));

    private const int LogoDecodeWidth = 400;
    private static readonly TimeSpan Crossfade = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan LogoFade = TimeSpan.FromMilliseconds(200);

    private readonly BackdropLayer[] _layers;
    private readonly CrossfadeLayers<BrowseHeroTitle> _crossfade = new();
    private readonly DispatcherQueueTimer _fadeDone;
    // The text's sizes as the markup gives them, for a 1080 px window; a taller one grows them.
    private readonly (double LogoWidth, double Name, double Meta, double Overview, double OverviewLine, double OverviewWidth, double Width, double Top) _textAt1080;
    private FrameworkElement? _page;
    // The fold's numbers, which its expressions read: a page of another height sets them anew, the expressions run on.
    private CompositionPropertySet? _fold;
    private CancellationTokenSource? _logoLoading;
    private BrowseHeroTitle? _shown;
    private string? _picturesOf;
    private ScrollViewer? _scroller;
    private double? _scrollTarget;
    private bool _isFolded;
    // The page was left: nothing loads into it until it shows again.
    private bool _suspended;
    private bool _showingPictures;

    public BrowseHero()
    {
        InitializeComponent();
        _textAt1080 = (Logo.MaxWidth, HeadingText.FontSize, MetaText.FontSize, OverviewText.FontSize, OverviewText.LineHeight, OverviewText.MaxWidth,
            Texts.MaxWidth, Texts.Margin.Top);
        ApplySizes(Sizes);
        _layers = [new BackdropLayer(LayerA, BlurA, ArtA), new BackdropLayer(LayerB, BlurB, ArtB)];
        foreach (var element in new UIElement[] { LayerA, LayerB, ArtA, ArtB })
        {
            element.OpacityTransition = new ScalarTransition { Duration = Crossfade };
        }
        Logo.OpacityTransition = new ScalarTransition { Duration = LogoFade };
        _fadeDone = DispatcherQueue.CreateTimer();
        _fadeDone.Interval = Crossfade;
        _fadeDone.IsRepeating = false;
        _fadeDone.Tick += (_, _) => FinishFade();
        SizeChanged += (_, e) => OnWidthChanged(e.NewSize.Width);
        // A title shown while the page was out of the window gets its pictures now that it is in it. The page's height sets
        // the hero's.
        Loaded += (_, _) =>
        {
            FollowPage();
            ShowPictures();
        };
        Visibility = Visibility.Collapsed;
    }

    /// <summary>The title shown; null hides the hero and lets go of its pictures.</summary>
    public BrowseHeroTitle? Title
    {
        get => (BrowseHeroTitle?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The hero's sizes on its page now, for the page's rows (<see cref="RowsViewMargin"/>, <see cref="RowsPadding"/>).</summary>
    public BrowseHeroSizes Sizes
    {
        get => (BrowseHeroSizes)GetValue(SizesProperty);
        private set => SetValue(SizesProperty, value);
    }

    /// <summary>
    /// The heading level the title's name is read at: the page's own heading on Home, which has no title of its own; none
    /// on a page with its own title, which the hero comes before.
    /// </summary>
    public AutomationHeadingLevel HeadingLevel
    {
        get => AutomationProperties.GetHeadingLevel(HeadingText);
        set => AutomationProperties.SetHeadingLevel(HeadingText, value);
    }

    // Static because the pages' x:Bind function bindings call them on the type. With a hero, the rows' view begins at the
    // compact header and the rows a fold's length below it, so at rest they start under the full hero and scroll up under
    // the header as it folds; without one (loading, empty, an error), as on any page.

    /// <summary>Where the rows' scroller begins under a hero for <paramref name="hero"/> of <paramref name="sizes"/>.</summary>
    public static Thickness RowsViewMargin(BrowseHeroTitle? hero, BrowseHeroSizes sizes) => new(0, hero is null ? 0 : sizes.CompactHeight, 0, 0);

    /// <summary>The padding of the rows in that scroller: the pages' 48 px sides and bottom, and the fold's length on top.</summary>
    public static Thickness RowsPadding(BrowseHeroTitle? hero, BrowseHeroSizes sizes) => new(48, hero is null ? 32 : sizes.CollapseDistance, 48, 48);

    /// <summary>
    /// Folds the hero as <paramref name="scroller"/> scrolls its first <see cref="BrowseHeroSizes.CollapseDistance"/>: the logo
    /// shrinks, the details move up under it, the overview fades, and the backdrop shortens to the compact header. On the
    /// compositor, so a scroll costs the UI thread nothing. A wheel over the hero, or a touch drag on it, scrolls the rows.
    /// </summary>
    public void FollowScroll(ScrollViewer scroller)
    {
        if (_scroller is not null)
        {
            return;
        }
        _scroller = scroller;
        scroller.ViewChanged += OnScrolled;
        var scroll = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(scroller);
        var compositor = scroll.Compositor;
        var fold = compositor.CreatePropertySet();
        _fold = fold;
        var expressions = BrowseHeroLayout.Expressions.ToDictionary(e => e.Target, e => e.Text, StringComparer.Ordinal);
        ExpressionAnimation Expression(string target)
        {
            var animation = compositor.CreateExpressionAnimation(expressions[target]);
            animation.SetReferenceParameter("scroll", scroll);
            animation.SetReferenceParameter("p", fold);
            return animation;
        }

        SetFoldNumbers();

        Visual(Heading).StartAnimation("Scale", Expression("Heading.Scale"));
        foreach (var text in new UIElement[] { MetaText, OverviewText })
        {
            ElementCompositionPreview.SetIsTranslationEnabled(text, true);
            Visual(text).StartAnimation("Translation.Y", Expression("Text.Translation.Y"));
        }
        Visual(OverviewText).StartAnimation("Opacity", Expression("Overview.Opacity"));
        var clip = compositor.CreateInsetClip();
        clip.StartAnimation("BottomInset", Expression("Backdrop.Clip.BottomInset"));
        Visual(BackdropBox).Clip = clip;
        ElementCompositionPreview.SetIsTranslationEnabled(BottomFade, true);
        var fade = Visual(BottomFade);
        fade.StartAnimation("Translation.Y", Expression("Fade.Translation.Y"));
        fade.StartAnimation("Scale", Expression("Fade.Scale"));
    }

    // The fold's numbers for the hero's sizes now; the bottom fade shrinks toward its own bottom.
    private void SetFoldNumbers()
    {
        if (_fold is not { } fold)
        {
            return;
        }
        foreach (var (name, value) in BrowseHeroLayout.ParametersFor(Sizes))
        {
            fold.InsertScalar(name, value);
        }
        Visual(BottomFade).CenterPoint = new Vector3(0, (float)Sizes.FadeHeight, 0);
    }

    /// <summary>The page was left: lets go of the pictures, and loads none until <see cref="Resume"/> brings them back.</summary>
    public void Suspend()
    {
        _suspended = true;
        Release();
    }

    /// <summary>The page shows again: the pictures of the title shown come back.</summary>
    public void Resume()
    {
        _suspended = false;
        Show(Title);
    }

    private static Visual Visual(UIElement element) => ElementCompositionPreview.GetElementVisual(element);

    private void Show(BrowseHeroTitle? title)
    {
        _shown = title;
        if (title is null)
        {
            Release();
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        HeadingText.Text = title.Heading;
        MetaText.Text = title.Meta;
        MetaText.Visibility = title.Meta.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        OverviewText.Text = title.Overview ?? "";
        OverviewText.Visibility = title.Overview is null ? Visibility.Collapsed : Visibility.Visible;
        ShowPictures();
    }

    // The logo and the backdrop of the title shown. Only while the hero is in the window: a cached page shows its title
    // again before it is back in the window (Resume from OnNavigatedTo), and a picture set then never raised ImageOpened,
    // so the logo and the backdrop stayed hidden behind the name and the blur. Loaded calls this again. Not while the page
    // is away either: a title that settles or reloads then would load into a page nothing lets go of. The same title
    // loaded again (its picture addresses are signed anew) keeps the pictures shown; one that comes while the pictures
    // are being set follows them, rather than starting over from within.
    private void ShowPictures()
    {
        if (_showingPictures)
        {
            return;
        }
        _showingPictures = true;
        try
        {
            while (!_suspended && IsLoaded && _shown is { } title && title.Key != _picturesOf)
            {
                _picturesOf = title.Key;
                ShowLogo(title.LogoUrl);
                Begin(title);
            }
        }
        finally
        {
            _showingPictures = false;
        }
    }

    // The name shows until the logo is there; a logo that fails leaves the name.
    private void ShowLogo(Uri? url)
    {
        BackdropLayer.Replace(ref _logoLoading);
        Logo.Opacity = 0;
        Logo.Source = Artwork.Load(url, LogoDecodeWidth, _logoLoading!.Token);
        HeadingText.Opacity = 1;
    }

    private void OnLogoOpened(object sender, RoutedEventArgs e)
    {
        Logo.Opacity = 1;
        HeadingText.Opacity = 0;
    }

    private void OnLogoFailed(object sender, ExceptionRoutedEventArgs e)
    {
        Logo.Source = null;
        HeadingText.Opacity = 1;
    }

    // A backdrop fades in on the layer not shown, on top; during a fade the newest title waits for it.
    private void Begin(BrowseHeroTitle title)
    {
        if (_crossfade.Begin(title) is not int front)
        {
            return;
        }
        var layer = _layers[front];
        var under = _layers[1 - front];
        layer.Fill(title, BackdropWidth(), _isFolded);
        Canvas.SetZIndex(layer.Grid, 1);
        Canvas.SetZIndex(under.Grid, 0);
        layer.Grid.Opacity = 1;
        _fadeDone.Start();
    }

    // The new layer is fully shown: the one under it hides and lets go of its picture, and a title that waited follows.
    // Otherwise the width that came during the fade is taken now.
    private void FinishFade()
    {
        var (hide, next) = _crossfade.Complete();
        if (hide is int index)
        {
            _layers[index].Grid.Opacity = 0;
            _layers[index].Clear();
        }
        if (next is not null && next.Key == _picturesOf)
        {
            Begin(next);
            return;
        }
        FollowWidth();
    }

    // A layer's picture fades in over its blur once it is decoded.
    private void OnArtOpened(object sender, RoutedEventArgs e) => ((Image)sender).Opacity = 1;

    private void Release()
    {
        _fadeDone.Stop();
        _crossfade.Reset();
        foreach (var layer in _layers)
        {
            layer.Grid.Opacity = 0;
            layer.Clear();
        }
        BackdropLayer.Replace(ref _logoLoading, renew: false);
        Logo.Source = null;
        _picturesOf = null;
    }

    private int BackdropWidth() => BrowseHeroLayout.BackdropDecodeWidth(BackdropBox.Width, Sizes.RestHeight);

    // The backdrop takes its share of the new width.
    private void OnWidthChanged(double width)
    {
        BackdropBox.Width = BrowseHeroLayout.BackdropBoxWidth(width, Sizes);
        FollowWidth();
    }

    // The page the hero is on: its height sets the hero's sizes. It must be the page's own full-height grid: a parent sized
    // to its content would take its height from the hero's, which the sizes set, and lay itself out in a loop.
    private void FollowPage()
    {
        if (_page is null && VisualTreeHelper.GetParent(this) is FrameworkElement page)
        {
            _page = page;
            page.SizeChanged += (_, e) => FollowPageHeight(e.NewSize.Height);
        }
        if (_page is not null)
        {
            FollowPageHeight(_page.ActualHeight);
        }
    }

    // A page of another height (in steps, BrowseHeroLayout.For): the hero takes its sizes, the backdrop its box (decoded
    // again when it grew notably), the fold its new numbers and where it counts as folded, and the page lays its rows under
    // it again (Sizes).
    private void FollowPageHeight(double height)
    {
        var sizes = BrowseHeroLayout.For(height);
        if (sizes == Sizes)
        {
            return;
        }
        Sizes = sizes;
        ApplySizes(sizes);
        OnWidthChanged(ActualWidth);
        SetFoldNumbers();
        FollowFold();
    }

    private void ApplySizes(BrowseHeroSizes sizes)
    {
        var text = _textAt1080;
        Root.Height = sizes.RestHeight;
        BottomFade.Height = sizes.FadeHeight;
        Heading.Height = sizes.HeadingHeight;
        Logo.MaxWidth = BrowseHeroLayout.Scaled(text.LogoWidth, sizes.Grow);
        HeadingText.FontSize = BrowseHeroLayout.Scaled(text.Name, sizes.Grow);
        Texts.MaxWidth = BrowseHeroLayout.Scaled(text.Width, sizes.Grow);
        Texts.Margin = Texts.Margin with { Top = BrowseHeroLayout.Scaled(text.Top, sizes.Grow) };
        MetaText.FontSize = BrowseHeroLayout.Scaled(text.Meta, sizes.TextGrow);
        OverviewText.FontSize = BrowseHeroLayout.Scaled(text.Overview, sizes.TextGrow);
        OverviewText.LineHeight = BrowseHeroLayout.Scaled(text.OverviewLine, sizes.TextGrow);
        OverviewText.MaxWidth = BrowseHeroLayout.Scaled(text.OverviewWidth, sizes.Grow);
    }

    // A picture not decoded yet is decoded now, fade or not; one decoded notably narrower fades in again at the new width.
    private void FollowWidth()
    {
        if (_crossfade.Front < 0 || _shown is null)
        {
            return;
        }
        var front = _layers[_crossfade.Front];
        int needed = BackdropWidth();
        switch (BrowseHeroLayout.StepFor(front.WaitsForArt, _isFolded, _crossfade.IsFading, front.DecodedWidth, needed))
        {
            case BackdropStep.LoadArt:
                front.LoadArt(needed);
                break;
            case BackdropStep.DecodeAgain when front.Title is { } title:
                Begin(title);
                break;
        }
    }

    private void OnScrolled(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate)
        {
            _scrollTarget = null;
        }
        FollowFold();
    }

    // Folded past half the way, a new backdrop shows only its blur; unfolding brings the picture.
    private void FollowFold()
    {
        if (_scroller is not { } scroller)
        {
            return;
        }
        bool folded = scroller.VerticalOffset > Sizes.CollapseDistance / 2;
        if (folded == _isFolded)
        {
            return;
        }
        _isFolded = folded;
        if (!folded && _crossfade.Front >= 0 && _layers[_crossfade.Front] is { WaitsForArt: true } front)
        {
            front.LoadArt(BackdropWidth());
        }
    }

    // A wheel over the header scrolls the rows: from where the last notch was going, so quick notches add up. A tilt and
    // Ctrl with the wheel are left to the page.
    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root).Properties;
        if (_scroller is not { } scroller || point.IsHorizontalMouseWheel || e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
        {
            return;
        }
        ScrollTo(scroller, BrowseHeroLayout.WheelTarget(scroller.VerticalOffset, _scrollTarget, point.MouseWheelDelta, scroller.ScrollableHeight));
        e.Handled = true;
    }

    // A touch or pen drag on the header scrolls the rows as one on them would; the mouse does not drag.
    private void OnDragStarting(object sender, ManipulationStartedRoutedEventArgs e)
    {
        if (e.PointerDeviceType == PointerDeviceType.Mouse || _scroller is null)
        {
            e.Complete();
        }
    }

    private void OnDrag(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (_scroller is { } scroller)
        {
            ScrollTo(scroller, BrowseHeroLayout.WheelTarget(scroller.VerticalOffset, _scrollTarget, (int)Math.Round(e.Delta.Translation.Y), scroller.ScrollableHeight));
            e.Handled = true;
        }
    }

    private void ScrollTo(ScrollViewer scroller, double offset)
    {
        _scrollTarget = offset;
        scroller.ChangeView(null, offset, null, disableAnimation: true);
    }
}
