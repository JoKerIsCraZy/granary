using System.Numerics;
using Granary.Core.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

namespace Granary.App.Controls;

/// <summary>
/// The top of Home: <see cref="Title"/>'s logo or name, its details and overview, over its backdrop, as Silage's home
/// shows the card in focus. A new title's backdrop fades in over the last one (its blur first). Once the rows below
/// scroll, the hero folds into a compact header of the logo and the details, which stays at the top (<see cref="FollowScroll"/>).
/// </summary>
public sealed partial class HomeHero : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(HomeHeroTitle), typeof(HomeHero),
            new PropertyMetadata(null, (sender, e) => ((HomeHero)sender).Show(e.NewValue as HomeHeroTitle)));

    /// <summary>The page's own height of the hero at rest; the backdrop reaches down behind the first row.</summary>
    public const double RestHeight = 440;

    /// <summary>The compact header's height, where the rows are cut off once they scroll.</summary>
    public const double CompactHeight = 150;

    /// <summary>How far the rows scroll before the hero is compact: the rows begin this far below the compact header.</summary>
    public const double CollapseDistance = 150;

    // The backdrop's share of the hero's width, from the right.
    private const double BackdropShare = 0.65;
    // Logo or name: 80 px high at rest, 45 compact.
    private const double CompactHeadingScale = 45.0 / 80;
    private const double HeadingHeight = 80;
    private const int LogoDecodeWidth = 400;
    private const int FallbackBackdropWidth = 1280;
    private static readonly TimeSpan Crossfade = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan LogoFade = TimeSpan.FromMilliseconds(200);

    private CancellationTokenSource? _loading;
    private string? _picturesOf;
    private bool _showsLayerA = true;
    private ScrollViewer? _scroller;

    public HomeHero()
    {
        InitializeComponent();
        foreach (var element in new UIElement[] { LayerA, LayerB, ArtA, ArtB })
        {
            element.OpacityTransition = new ScalarTransition { Duration = Crossfade };
        }
        Logo.OpacityTransition = new ScalarTransition { Duration = LogoFade };
        SizeChanged += (_, e) => BackdropBox.Width = Math.Round(e.NewSize.Width * BackdropShare);
        Visibility = Visibility.Collapsed;
    }

    /// <summary>The title shown; null hides the hero.</summary>
    public HomeHeroTitle? Title
    {
        get => (HomeHeroTitle?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// Folds the hero as <paramref name="scroller"/> scrolls its first <see cref="CollapseDistance"/>: the logo shrinks, the details
    /// move up under it, the overview fades, and the backdrop shortens to the compact header. On the compositor, so a scroll
    /// costs the UI thread nothing. A mouse wheel over the hero scrolls the rows too.
    /// </summary>
    public void FollowScroll(ScrollViewer scroller)
    {
        if (_scroller is not null)
        {
            return;
        }
        _scroller = scroller;
        var scroll = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(scroller);
        var compositor = scroll.Compositor;
        string progress = $"Clamp(-scroll.Translation.Y / {CollapseDistance}, 0, 1)";
        ExpressionAnimation Expression(string text)
        {
            var animation = compositor.CreateExpressionAnimation(text);
            animation.SetReferenceParameter("scroll", scroll);
            return animation;
        }

        double shrink = 1 - CompactHeadingScale;
        Visual(Heading).StartAnimation("Scale", Expression($"Vector3(1 - {shrink} * {progress}, 1 - {shrink} * {progress}, 1)"));
        double rise = HeadingHeight * shrink;
        foreach (var text in new UIElement[] { MetaText, OverviewText })
        {
            ElementCompositionPreview.SetIsTranslationEnabled(text, true);
            Visual(text).StartAnimation("Translation.Y", Expression($"-{rise} * {progress}"));
        }
        Visual(OverviewText).StartAnimation("Opacity", Expression($"Clamp(1 - 2 * {progress}, 0, 1)"));

        // The backdrop is cut short from below, and its bottom fade rises and narrows with the cut, its end a little past the
        // cut so no line of the picture shows under it.
        double cut = RestHeight - CompactHeight;
        double fadeRise = cut - 2;
        var clip = compositor.CreateInsetClip();
        clip.StartAnimation("BottomInset", Expression($"{cut} * {progress}"));
        Visual(BackdropBox).Clip = clip;
        double fadeHeight = BottomFade.Height;
        double compactFade = CompactHeight * 0.55;
        ElementCompositionPreview.SetIsTranslationEnabled(BottomFade, true);
        var fade = Visual(BottomFade);
        fade.CenterPoint = new Vector3(0, (float)fadeHeight, 0);
        fade.StartAnimation("Translation.Y", Expression($"-{fadeRise} * {progress}"));
        fade.StartAnimation("Scale", Expression($"Vector3(1, 1 - {1 - compactFade / fadeHeight} * {progress}, 1)"));
    }

    private static Visual Visual(UIElement element) => ElementCompositionPreview.GetElementVisual(element);

    private void Show(HomeHeroTitle? title)
    {
        if (title is null)
        {
            _loading?.Cancel();
            _picturesOf = null;
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        HeadingText.Text = title.Heading;
        MetaText.Text = title.Meta;
        MetaText.Visibility = title.Meta.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        OverviewText.Text = title.Overview ?? "";
        OverviewText.Visibility = title.Overview is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(this, title.Heading);
        if (title.Key == _picturesOf)
        {
            // The same title loaded again (its picture addresses are signed anew): the pictures shown stay.
            return;
        }
        _picturesOf = title.Key;
        _loading?.Cancel();
        _loading = new CancellationTokenSource();
        ShowLogo(title.LogoUrl, _loading.Token);
        ShowBackdrop(title, _loading.Token);
    }

    // The name shows until the logo is there; a logo that fails leaves the name.
    private void ShowLogo(Uri? url, CancellationToken cancellationToken)
    {
        Logo.Opacity = 0;
        Logo.Source = Artwork.Load(url, LogoDecodeWidth, cancellationToken);
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

    // The new backdrop goes into the hidden layer, blur first, and that layer fades in over the shown one.
    private void ShowBackdrop(HomeHeroTitle title, CancellationToken cancellationToken)
    {
        _showsLayerA = !_showsLayerA;
        var (layer, blur, art) = _showsLayerA ? (LayerA, BlurA, ArtA) : (LayerB, BlurB, ArtB);
        var other = _showsLayerA ? LayerB : LayerA;
        art.Opacity = 0;
        blur.Source = Thumbhashes.Get(title.BackdropThumbhash);
        // Decoded as wide as it is shown (its width is NaN before the hero was laid out).
        int width = BackdropBox.Width > 0 ? (int)Math.Ceiling(BackdropBox.Width) : FallbackBackdropWidth;
        art.Source = Artwork.Load(title.BackdropUrl, width, cancellationToken);
        layer.Opacity = 1;
        other.Opacity = 0;
        Canvas.SetZIndex(layer, 1);
        Canvas.SetZIndex(other, 0);
    }

    private void OnArtOpened(object sender, RoutedEventArgs e) => ((Image)sender).Opacity = 1;

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_scroller is { } scroller)
        {
            int delta = e.GetCurrentPoint(Root).Properties.MouseWheelDelta;
            scroller.ChangeView(null, scroller.VerticalOffset - delta, null);
            e.Handled = true;
        }
    }
}
