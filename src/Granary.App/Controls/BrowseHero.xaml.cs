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

namespace Granary.App.Controls;

/// <summary>
/// The top of a page of rows (Home, For You): <see cref="Title"/>'s logo or name, its details and overview, over its
/// backdrop, as Silage's home shows the card in focus. A new title's backdrop fades in over the last one, its blur first;
/// the last one stays fully shown underneath until the fade is done. Once the rows below scroll, the hero folds into a
/// compact header of the logo and the details, which stays at the top (<see cref="FollowScroll"/>); folded, a new title
/// shows only its backdrop's blur. The page lays its rows under it with <see cref="RowsViewMargin"/> and
/// <see cref="RowsPadding"/>.
/// </summary>
public sealed partial class BrowseHero : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(BrowseHeroTitle), typeof(BrowseHero),
            new PropertyMetadata(null, (sender, e) => ((BrowseHero)sender).Show(e.NewValue as BrowseHeroTitle)));

    private const int LogoDecodeWidth = 400;
    private static readonly TimeSpan Crossfade = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan LogoFade = TimeSpan.FromMilliseconds(200);

    private readonly Layer[] _layers;
    private readonly CrossfadeLayers<BrowseHeroTitle> _crossfade = new();
    private readonly DispatcherQueueTimer _fadeDone;
    private CancellationTokenSource? _logoLoading;
    private BrowseHeroTitle? _shown;
    private string? _picturesOf;
    private ScrollViewer? _scroller;
    private double? _scrollTarget;
    private bool _isFolded;

    public BrowseHero()
    {
        InitializeComponent();
        Root.Height = BrowseHeroLayout.RestHeight;
        BottomFade.Height = BrowseHeroLayout.RestHeight * BrowseHeroLayout.FadeShare;
        _layers = [new Layer(LayerA, BlurA, ArtA), new Layer(LayerB, BlurB, ArtB)];
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
        // A title shown while the page was out of the window gets its pictures now: right after Loaded, not in it, as
        // setting them while the page was being put in the window overflowed the stack in WinUI.
        Loaded += (_, _) => DispatcherQueue.TryEnqueue(ShowPictures);
        Visibility = Visibility.Collapsed;
    }

    /// <summary>The title shown; null hides the hero and lets go of its pictures.</summary>
    public BrowseHeroTitle? Title
    {
        get => (BrowseHeroTitle?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// The heading level the title's name is read at: the page's own heading on Home, which has no title of its own; under
    /// the page's title elsewhere.
    /// </summary>
    public AutomationHeadingLevel HeadingLevel
    {
        get => AutomationProperties.GetHeadingLevel(HeadingText);
        set => AutomationProperties.SetHeadingLevel(HeadingText, value);
    }

    // Static because the pages' x:Bind function bindings call them on the type. With a hero, the rows' view begins at the
    // compact header and the rows a fold's length below it, so at rest they start under the full hero and scroll up under
    // the header as it folds; without one (loading, empty, an error), as on any page.

    /// <summary>Where the rows' scroller begins under a hero for <paramref name="hero"/>.</summary>
    public static Thickness RowsViewMargin(BrowseHeroTitle? hero) => new(0, hero is null ? 0 : BrowseHeroLayout.CompactHeight, 0, 0);

    /// <summary>The padding of the rows in that scroller: the pages' 48 px sides and bottom, and the fold's length on top.</summary>
    public static Thickness RowsPadding(BrowseHeroTitle? hero) => new(48, hero is null ? 32 : BrowseHeroLayout.CollapseDistance, 48, 48);

    /// <summary>
    /// Folds the hero as <paramref name="scroller"/> scrolls its first <see cref="BrowseHeroLayout.CollapseDistance"/>: the logo
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
        var expressions = BrowseHeroLayout.Expressions.ToDictionary(e => e.Target, e => e.Text, StringComparer.Ordinal);
        ExpressionAnimation Expression(string target)
        {
            var animation = compositor.CreateExpressionAnimation(expressions[target]);
            animation.SetReferenceParameter("scroll", scroll);
            foreach (var (name, value) in BrowseHeroLayout.Parameters)
            {
                animation.SetScalarParameter(name, value);
            }
            return animation;
        }

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
        fade.CenterPoint = new Vector3(0, (float)BottomFade.Height, 0);
        fade.StartAnimation("Translation.Y", Expression("Fade.Translation.Y"));
        fade.StartAnimation("Scale", Expression("Fade.Scale"));
    }

    /// <summary>The page was left: lets go of the pictures (<see cref="Resume"/> brings them back).</summary>
    public void Suspend() => Release();

    /// <summary>The page shows again: the pictures of the title shown come back.</summary>
    public void Resume() => Show(Title);

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
    // so the logo and the backdrop stayed hidden behind the name and the blur. Loaded calls this again.
    private void ShowPictures()
    {
        if (_shown is not { } title || !IsLoaded || title.Key == _picturesOf)
        {
            // The same title loaded again (its picture addresses are signed anew): the pictures shown stay.
            return;
        }
        _picturesOf = title.Key;
        ShowLogo(title.LogoUrl);
        Begin(title);
    }

    // The name shows until the logo is there; a logo that fails leaves the name.
    private void ShowLogo(Uri? url)
    {
        Replace(ref _logoLoading);
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
        Replace(ref _logoLoading, renew: false);
        Logo.Source = null;
        _picturesOf = null;
    }

    private int BackdropWidth() => BrowseHeroLayout.BackdropDecodeWidth(BackdropBox.Width);

    // The backdrop takes its share of the new width.
    private void OnWidthChanged(double width)
    {
        BackdropBox.Width = Math.Round(width * BrowseHeroLayout.BackdropShare);
        FollowWidth();
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

    // Folded past half the way, a new backdrop shows only its blur; unfolding brings the picture.
    private void OnScrolled(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_scroller is not { } scroller)
        {
            return;
        }
        if (!e.IsIntermediate)
        {
            _scrollTarget = null;
        }
        bool folded = scroller.VerticalOffset > BrowseHeroLayout.CollapseDistance / 2;
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

    private static void Replace(ref CancellationTokenSource? loading, bool renew = true)
    {
        loading?.Cancel();
        loading?.Dispose();
        loading = renew ? new CancellationTokenSource() : null;
    }

    /// <summary>One backdrop layer: its blur, its picture, and what it is loading.</summary>
    private sealed class Layer(Grid grid, Image blur, Image art)
    {
        private CancellationTokenSource? _loading;

        public Grid Grid { get; } = grid;

        public BrowseHeroTitle? Title { get; private set; }

        /// <summary>The picture waits: the width was not known yet, or the hero was folded.</summary>
        public bool WaitsForArt { get; private set; }

        public int DecodedWidth { get; private set; }

        public void Fill(BrowseHeroTitle title, int width, bool folded)
        {
            Clear();
            Title = title;
            blur.Source = Thumbhashes.Get(title.BackdropThumbhash);
            WaitsForArt = title.BackdropUrl is not null;
            if (!folded)
            {
                LoadArt(width);
            }
        }

        public void LoadArt(int width)
        {
            if (Title?.BackdropUrl is not { } url || width <= 0)
            {
                return;
            }
            Replace(ref _loading);
            art.Opacity = 0;
            art.Source = Artwork.Load(url, width, _loading!.Token);
            DecodedWidth = width;
            WaitsForArt = false;
        }

        public void Clear()
        {
            Replace(ref _loading, renew: false);
            blur.Source = null;
            art.Source = null;
            art.Opacity = 0;
            Title = null;
            WaitsForArt = false;
            DecodedWidth = 0;
        }
    }
}
