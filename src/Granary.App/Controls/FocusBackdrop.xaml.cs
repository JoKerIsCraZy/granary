using Granary.Core.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Granary.App.Controls;

/// <summary>
/// A quiet backdrop behind a page's header, as Silage's library page shows one: <see cref="Title"/>'s backdrop over the
/// right three quarters and the top 70 % of the page, dimmed into the page's colour toward the left and down. A new title
/// fades in over the last one in 400 ms, its blur first; none fades it out. Unlike <see cref="BrowseHero"/> it moves
/// nothing and shows no words: the page's content lies over it as before.
/// </summary>
public sealed partial class FocusBackdrop : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(BrowseHeroTitle), typeof(FocusBackdrop),
            new PropertyMetadata(null, (sender, e) => ((FocusBackdrop)sender).Show(e.NewValue as BrowseHeroTitle)));

    // The box's share of the page, from the top right, and the fade's length.
    private const double WidthShare = 0.75;
    private const double HeightShare = 0.7;
    private static readonly TimeSpan Crossfade = TimeSpan.FromMilliseconds(400);

    private readonly BackdropLayer[] _layers;
    private readonly CrossfadeLayers<BrowseHeroTitle> _crossfade = new();
    private readonly DispatcherQueueTimer _fadeDone;
    private readonly DispatcherQueueTimer _fadedOut;
    private BrowseHeroTitle? _shown;
    private string? _picturesOf;

    public FocusBackdrop()
    {
        InitializeComponent();
        _layers = [new BackdropLayer(LayerA, BlurA, ArtA), new BackdropLayer(LayerB, BlurB, ArtB)];
        foreach (var element in new UIElement[] { LayerA, LayerB, ArtA, ArtB })
        {
            element.OpacityTransition = new ScalarTransition { Duration = Crossfade };
        }
        _fadeDone = Timer(FinishFade);
        _fadedOut = Timer(ClearHidden);
        SizeChanged += (_, e) => OnSizeChanged(e.NewSize);
        // As BrowseHero: a title shown while the page was out of the window gets its picture right after Loaded, not in it.
        Loaded += (_, _) => DispatcherQueue.TryEnqueue(ShowPicture);
    }

    /// <summary>The title whose backdrop shows; null fades it out and lets go of the pictures.</summary>
    public BrowseHeroTitle? Title
    {
        get => (BrowseHeroTitle?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The page was left: lets go of the pictures (<see cref="Resume"/> brings them back).</summary>
    public void Suspend() => Release();

    /// <summary>The page shows again: the picture of the title shown comes back.</summary>
    public void Resume() => Show(Title);

    private DispatcherQueueTimer Timer(Action tick)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = Crossfade;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => tick();
        return timer;
    }

    private void Show(BrowseHeroTitle? title)
    {
        _shown = title;
        if (title is null)
        {
            FadeOut();
            return;
        }
        ShowPicture();
    }

    // Only while in the window (see BrowseHero.ShowPictures): a picture set before never raised ImageOpened.
    private void ShowPicture()
    {
        if (_shown is not { } title || !IsLoaded || title.Key == _picturesOf)
        {
            return;
        }
        if (_fadedOut.IsRunning)
        {
            // Pointed at again while the last picture fades out: that one goes at once.
            _fadedOut.Stop();
            ClearHidden();
        }
        _picturesOf = title.Key;
        Begin(title);
    }

    // A picture fades in on the layer not shown, on top; during a fade the newest title waits for it.
    private void Begin(BrowseHeroTitle title)
    {
        if (_crossfade.Begin(title) is not int front)
        {
            return;
        }
        var layer = _layers[front];
        layer.Fill(title, DecodeWidth(), folded: false);
        Canvas.SetZIndex(layer.Grid, 1);
        Canvas.SetZIndex(_layers[1 - front].Grid, 0);
        layer.Grid.Opacity = 1;
        _fadeDone.Start();
    }

    // The new layer is fully shown: the one under it hides and lets go of its picture, and a title that waited follows.
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
        FollowSize();
    }

    // Nothing pointed at any more: the picture fades out, then goes.
    private void FadeOut()
    {
        _fadeDone.Stop();
        _crossfade.Reset();
        _picturesOf = null;
        foreach (var layer in _layers)
        {
            layer.Grid.Opacity = 0;
        }
        _fadedOut.Start();
    }

    // The layers faded out let go of their pictures.
    private void ClearHidden()
    {
        foreach (var layer in _layers.Where(layer => layer.Grid.Opacity == 0))
        {
            layer.Clear();
        }
    }

    // A layer's picture fades in over its blur once it is decoded.
    private void OnArtOpened(object sender, RoutedEventArgs e) => ((Image)sender).Opacity = 1;

    private void Release()
    {
        _fadeDone.Stop();
        _fadedOut.Stop();
        _crossfade.Reset();
        foreach (var layer in _layers)
        {
            layer.Grid.Opacity = 0;
            layer.Clear();
        }
        _picturesOf = null;
    }

    private int DecodeWidth() => BrowseHeroLayout.BackdropDecodeWidth(BackdropBox.Width, BackdropBox.Height);

    private void OnSizeChanged(Size size)
    {
        BackdropBox.Width = Math.Round(size.Width * WidthShare);
        BackdropBox.Height = Math.Round(size.Height * HeightShare);
        FollowSize();
    }

    // A picture not decoded yet is decoded now; one decoded notably narrower fades in again at the new size.
    private void FollowSize()
    {
        if (_crossfade.Front < 0 || _shown is null)
        {
            return;
        }
        var front = _layers[_crossfade.Front];
        int needed = DecodeWidth();
        switch (BrowseHeroLayout.StepFor(front.WaitsForArt, folded: false, _crossfade.IsFading, front.DecodedWidth, needed))
        {
            case BackdropStep.LoadArt:
                front.LoadArt(needed);
                break;
            case BackdropStep.DecodeAgain when front.Title is { } title:
                Begin(title);
                break;
        }
    }
}
