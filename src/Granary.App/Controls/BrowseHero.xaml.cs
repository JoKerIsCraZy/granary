using System.Numerics;
using Granary.Core.Texts;
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
using Windows.Foundation;

namespace Granary.App.Controls;

/// <summary>
/// The top of a page of rows (Home, For You): <see cref="Title"/>'s logo or name, its details and overview, over its
/// backdrop, for the card in focus. A new title's backdrop fades in over the last one, its blur first;
/// the last one stays fully shown underneath until the fade is done. Once the rows below scroll, the hero folds into a
/// compact header of the logo and the details, which stays at the top (<see cref="FollowScroll"/>); folded, a new title
/// shows only its backdrop's blur. An unwatched episode's still, while the profile hides those, shows blurred as the web's
/// <c>HeroBanner</c> (<see cref="BrowseHeroTitle.BlursBackdrop"/>, <see cref="BackdropLayer"/>), and its overview not at all.
/// On a taller page the hero is taller (<see cref="Sizes"/>, <see cref="BrowseHeroLayout.For"/>). Set to
/// <see cref="BrowseHeroSize.Full"/> (<see cref="Size"/>; Settings → Navigation &amp; Cards → Hero size) it is as tall as the
/// banner of Silo's web: the rows begin under it, and its text stands at its foot and goes up with the rows into the header.
/// Showing Home's banner (<see cref="SlideNumber"/>), it says which slide over the name and begins the rows lower, for the
/// banner's buttons. The page lays its rows under it with <see cref="RowsViewMargin"/> and <see cref="RowsPadding"/>.
/// With the title preview off (Settings → Navigation &amp; Cards, <see cref="BrowseHeroSetting"/>) the page gives it no title
/// but Home's banner, so it stays hidden, holds no picture and the rows start at the top.
/// </summary>
public sealed partial class BrowseHero : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(BrowseHeroTitle), typeof(BrowseHero),
            new PropertyMetadata(null, (sender, e) => ((BrowseHero)sender).Show(e.NewValue as BrowseHeroTitle)));

    public static readonly DependencyProperty SlideNumberProperty =
        DependencyProperty.Register(nameof(SlideNumber), typeof(string), typeof(BrowseHero),
            new PropertyMetadata(null, (sender, e) => ((BrowseHero)sender).ShowSlideNumber(e.NewValue as string)));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(BrowseHeroSize), typeof(BrowseHero),
            new PropertyMetadata(BrowseHeroSize.Compact, (sender, _) => ((BrowseHero)sender).FollowPageHeight()));

    public static readonly DependencyProperty SizesProperty =
        DependencyProperty.Register(nameof(Sizes), typeof(BrowseHeroSizes), typeof(BrowseHero), new PropertyMetadata(BrowseHeroLayout.For(0)));

    private const int LogoDecodeWidth = 400;
    // A rating's mark beside its score, as the web's RatingEntry sets them: 12 px beside 14 px. The score is the line's size.
    private const double RatingMarkShare = 12d / 14;
    private static readonly TimeSpan Crossfade = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan LogoFade = TimeSpan.FromMilliseconds(200);

    private readonly BackdropLayer[] _layers;
    private readonly CrossfadeLayers<BrowseHeroTitle> _crossfade = new();
    private readonly DispatcherQueueTimer _fadeDone;
    // The backdrop's box, which cuts off what of the picture is over its edges.
    private readonly RectangleGeometry _pictureClip = new();
    // The text's sizes as the markup gives them, for a 1080 px window; a taller one grows them.
    private readonly (double LogoWidth, double Name, double Meta, double Overview, double OverviewLine, double OverviewWidth, double Width, double Top) _textAt1080;
    private readonly (double Size, double Line, double Gap) _eyebrowAt1080;
    private FrameworkElement? _page;
    // The fold's numbers, which its expressions read: a page of another height sets them anew, the expressions run on.
    private CompositionPropertySet? _fold;
    private CancellationTokenSource? _logoLoading;
    private BrowseHeroTitle? _shown;
    // The title whose pictures show, whether its backdrop is blurred and which one it is (BrowseHeroTitle.Pictures).
    private (string Key, bool Blurs, string? Backdrop)? _picturesOf;
    private ScrollViewer? _scroller;
    private double? _scrollTarget;
    private bool _isFolded;
    // The page was left: nothing loads into it until it shows again.
    private bool _suspended;
    private bool _showingPictures;

    public BrowseHero()
    {
        InitializeComponent();
        // The web's eyebrow, in capitals as its CSS shows it.
        EyebrowLabel.Text = AppText.Upper(AppText.Get("web.home.hero.eyebrow"));
        _textAt1080 = (Logo.MaxWidth, HeadingText.FontSize, MetaText.FontSize, OverviewText.FontSize, OverviewText.LineHeight, OverviewText.MaxWidth,
            Texts.MaxWidth, Texts.Margin.Top);
        _eyebrowAt1080 = (Eyebrow.FontSize, Eyebrow.LineHeight, Eyebrow.Margin.Bottom);
        Pictures.Clip = _pictureClip;
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
        WeakTick.Subscribe(_fadeDone, this, static hero => hero.FinishFade());
        SizeChanged += (_, e) => OnWidthChanged(e.NewSize.Width);
        // Another title's text is another height, and the full hero's text has another way up to the header. Only a number
        // of the fold changes here, nothing of the layout.
        Texts.SizeChanged += (_, _) => SetFoldNumbers();
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

    /// <summary>
    /// Home's banner: the slide shown, "01", for "FEATURED — NO. 01" over its name; null or empty for a hero that follows the
    /// cards.
    /// </summary>
    public string? SlideNumber
    {
        get => (string?)GetValue(SlideNumberProperty);
        set => SetValue(SlideNumberProperty, value);
    }

    /// <summary>
    /// Compact, Granary's own sizes, or Full, as tall as the banner of Silo's web: the profile's choice in Settings, which the
    /// page hands on as it shows.
    /// </summary>
    public BrowseHeroSize Size
    {
        get => (BrowseHeroSize)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
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
    // the header as it folds; without one (the title preview off, loading, empty, an error), at the top. They take whether
    // the page has a title, not the title: x:Bind calls a function again only for an argument that is not null, so a title
    // that went would have left its room behind (BrowseHeroViewModel.HasTitle).

    /// <summary>
    /// How far down a page of rows begins without a hero: the web's Home, For You and Requests lay out their own top, 24 px
    /// (<c>pt-6</c>), rather than the 32 px of its other pages.
    /// </summary>
    public const double TopWithoutHero = 24;

    /// <summary>Where the rows' scroller begins under a hero of <paramref name="sizes"/>, while the page <paramref name="hasHero"/>.</summary>
    public static Thickness RowsViewMargin(bool hasHero, BrowseHeroSizes sizes) => new(0, hasHero ? sizes.CompactHeight : 0, 0, 0);

    /// <summary>
    /// The padding of the rows in that scroller: the pages' 48 px sides and bottom, and the fold's length on top
    /// (<see cref="TopWithoutHero"/> without a hero).
    /// </summary>
    public static Thickness RowsPadding(bool hasHero, BrowseHeroSizes sizes) =>
        new(48, hasHero ? sizes.CollapseDistance : TopWithoutHero, 48, 48);

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

        ElementCompositionPreview.SetIsTranslationEnabled(Texts, true);
        Visual(Texts).StartAnimation("Translation.Y", Expression("Texts.Translation.Y"));
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

    // The fold's numbers for the hero's sizes and its text's height now; the bottom fade shrinks toward its own bottom.
    private void SetFoldNumbers()
    {
        if (_fold is not { } fold)
        {
            return;
        }
        double lift = BrowseHeroLayout.TextLift(Sizes, Texts.ActualHeight, Texts.Margin.Top);
        foreach (var (name, value) in BrowseHeroLayout.ParametersFor(Sizes, lift))
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
        ShowDetails(title);
        OverviewText.Text = title.Overview ?? "";
        OverviewText.Visibility = title.Overview is null ? Visibility.Collapsed : Visibility.Visible;
        ShowPictures();
    }

    // The line of details: what comes before its rating, the rating's mark and score in their own type, and the rest. A line
    // without a rating is all in the first run. It is read whole, as it would be written ("2006 · IMDb 8.4 · Drama · TV-PG").
    private void ShowDetails(BrowseHeroTitle title)
    {
        MetaLead.Text = title.MetaBeforeRating;
        MetaMark.Text = title.Rating?.Name ?? "";
        MetaScore.Text = title.Rating is { } rating ? " " + rating.Display : "";
        MetaTail.Text = title.MetaAfterRating;
        AutomationProperties.SetName(MetaText, title.Meta);
        MetaText.Visibility = title.Meta.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Whether the hero shows Home's banner. x:Bind hands a string property "" for null, so empty means none too: the card
    // hero showed "FEATURED — NO." over its title, and its rows began the banner's room lower.
    private bool IsBanner => !string.IsNullOrEmpty(SlideNumber);

    // The banner's eyebrow, read as the web's two texts beside each other ("Featured — No. 01"). The banner coming or going
    // moves the rows.
    private void ShowSlideNumber(string? number)
    {
        bool isBanner = !string.IsNullOrEmpty(number);
        string slide = isBanner ? AppText.Get("web.home.hero.slideNumber", ("number", number)) : "";
        Eyebrow.Visibility = isBanner ? Visibility.Visible : Visibility.Collapsed;
        EyebrowNumber.Text = AppText.Upper(slide);
        AutomationProperties.SetName(Eyebrow, isBanner ? $"{AppText.Get("web.home.hero.eyebrow")} — {slide}" : "");
        FollowPageHeight();
    }

    // The logo and the backdrop of the title shown. Only while the hero is in the window: a cached page shows its title
    // again before it is back in the window (Resume from OnNavigatedTo), and a picture set then never raised ImageOpened,
    // so the logo and the backdrop stayed hidden behind the name and the blur. Loaded calls this again. Not while the page
    // is away either: a title that settles or reloads then would load into a page nothing lets go of. The same title
    // loaded again (its picture addresses are signed anew) keeps the pictures shown; one that comes while the pictures
    // are being set follows them, rather than starting over from within. The same title with its backdrop blurred now, or
    // no longer (a spoiler switch, the episode started), or with another backdrop (a wider one, after its page asked for
    // them again), keeps its logo and fades its backdrop over.
    private void ShowPictures()
    {
        if (_showingPictures)
        {
            return;
        }
        _showingPictures = true;
        try
        {
            while (!_suspended && IsLoaded && _shown is { } title && PicturesOf(title) != _picturesOf)
            {
                if (title.Key != _picturesOf?.Key)
                {
                    ShowLogo(title.LogoUrl);
                }
                _picturesOf = PicturesOf(title);
                Begin(title);
            }
        }
        finally
        {
            _showingPictures = false;
        }
    }

    private static (string Key, bool Blurs, string? Backdrop) PicturesOf(BrowseHeroTitle title) => title.Pictures;

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

    // A backdrop fades in on the layer not shown, on top; during a fade the newest title waits for it. A title whose still is
    // blurred now takes its sharp picture away at once, also while it waits, rather than fading over it.
    private void Begin(BrowseHeroTitle title)
    {
        if (title.BlursBackdrop)
        {
            foreach (var shown in _layers)
            {
                if (shown.Title is { BlursBackdrop: false } sharp && sharp.Key == title.Key)
                {
                    shown.Clear();
                }
            }
        }
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
        if (next is not null && PicturesOf(next) == _picturesOf)
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

    // The backdrop takes its share of the new width, or all of it under the full hero.
    private void OnWidthChanged(double width)
    {
        BackdropBox.Width = BrowseHeroLayout.BackdropBoxWidth(width, Sizes);
        PlacePicture();
        FollowWidth();
    }

    // The picture fills the box, showing its upper part; what is over the box's edges is cut off.
    private void PlacePicture()
    {
        double width = BackdropBox.Width;
        var place = BrowseHeroLayout.PlaceBackdrop(width, Sizes.RestHeight);
        Picture.Width = place.Width;
        Picture.Height = place.Height;
        Canvas.SetLeft(Picture, place.Left);
        Canvas.SetTop(Picture, place.Top);
        _pictureClip.Rect = new Rect(0, 0, double.IsFinite(width) ? Math.Max(0, width) : 0, Sizes.RestHeight);
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

    // The banner come or gone, or another size chosen: the sizes for the page's height as it is, if it is known yet.
    private void FollowPageHeight() => FollowPageHeight(_page?.ActualHeight ?? double.NaN);

    // A page of another height (in steps, BrowseHeroLayout.For), the banner come or gone, or another size chosen: the hero
    // takes its sizes, the backdrop its box (decoded again when it grew notably), the fold its new numbers and where it
    // counts as folded, and the page lays its rows under it again (Sizes).
    private void FollowPageHeight(double height)
    {
        var sizes = BrowseHeroLayout.For(height, banner: IsBanner, Size);
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
        // The compact hero's backdrop fades into the page on its left; the full one's lies under the web's banner's fades.
        SideFade.Fill = (Brush)Resources[sizes.IsFull ? "FullSideFade" : "CompactSideFade"];
        BottomFade.Fill = (Brush)Resources[sizes.IsFull ? "FullBottomFade" : "CompactBottomFade"];
        Dim.Visibility = sizes.IsFull ? Visibility.Visible : Visibility.Collapsed;
        Heading.Height = sizes.HeadingHeight;
        Logo.MaxWidth = BrowseHeroLayout.Scaled(text.LogoWidth, sizes.Grow);
        HeadingText.FontSize = BrowseHeroLayout.Scaled(text.Name, sizes.Grow);
        Texts.MaxWidth = BrowseHeroLayout.Scaled(text.Width, sizes.Grow);
        // The compact hero's text hangs from the top; the full one's stands on its foot, as the web's banner's, and never
        // higher than the header has it.
        Texts.VerticalAlignment = sizes.TextBottom is null ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        Texts.Margin = Texts.Margin with { Top = BrowseHeroLayout.Scaled(text.Top, sizes.Grow), Bottom = sizes.TextBottom ?? 0 };
        MetaText.FontSize = BrowseHeroLayout.Scaled(text.Meta, sizes.TextGrow);
        MetaMark.FontSize = BrowseHeroLayout.Scaled(text.Meta * RatingMarkShare, sizes.TextGrow);
        OverviewText.FontSize = BrowseHeroLayout.Scaled(text.Overview, sizes.TextGrow);
        OverviewText.LineHeight = BrowseHeroLayout.Scaled(text.OverviewLine, sizes.TextGrow);
        OverviewText.MaxWidth = BrowseHeroLayout.Scaled(text.OverviewWidth, sizes.Grow);
        var eyebrow = _eyebrowAt1080;
        Eyebrow.FontSize = Math.Round(eyebrow.Size * sizes.TextGrow, 1);
        Eyebrow.LineHeight = BrowseHeroLayout.Scaled(eyebrow.Line, sizes.TextGrow);
        Eyebrow.Margin = Eyebrow.Margin with { Bottom = BrowseHeroLayout.Scaled(eyebrow.Gap, sizes.Grow) };
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
        bool blurred = front.Title is { BlursBackdrop: true };
        switch (BrowseHeroLayout.StepFor(front.WaitsForArt, _isFolded, _crossfade.IsFading, blurred, front.DecodedWidth, needed))
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
        bool folded = BrowseHeroLayout.IsFolded(scroller.VerticalOffset, Sizes);
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
