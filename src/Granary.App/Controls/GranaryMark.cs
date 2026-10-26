using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Granary.App.Controls;

/// <summary>
/// Granary's mark (Assets/Logo/granary-mark.svg), the G ring from red to gold around the play triangle, drawn on the
/// compositor: still, playing its intro (the ring draws itself with a quarter turn, play pops in, a glint runs round it)
/// or, while <see cref="IsLoading"/>, its loading loop (a stretch of the ring runs through the G while play breathes).
/// The animations run on the compositor, so they stay smooth while the UI thread is busy, as it is at start and while a
/// title loads. With Windows' animations off the mark stands still.
/// </summary>
public sealed partial class GranaryMark : Grid
{
    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(GranaryMark),
            new PropertyMetadata(false, (sender, _) => ((GranaryMark)sender).ShowLoading()));

    // The mark's units, as its SVG less its 40 margin: 176 square, the ring a circle of radius 68 around the middle, 30 wide.
    private const float Units = 176;
    private const float Middle = 88;
    private const float Radius = 68;
    private const float RingWidth = 30;

    // The G as a share of the circle, which composition runs clockwise from 12 o'clock. Its lower end is 20° below 3
    // o'clock, so the trimming starts there (TrimOffset, as the G passes 12 o'clock); from it the G runs 300° round to 40°
    // above 3 o'clock. The stroke starts at that upper end, as the SVG path does, and is drawn back from there.
    private const float ArcStart = 110f / 360;
    private const float ArcLow = 0;
    private const float ArcHigh = 300f / 360;

    // The intro's timing, as granary-mark-intro.svg: the ring in 950 ms, play from 620 ms, the glint from 1020 ms.
    private static readonly TimeSpan DrawDuration = TimeSpan.FromMilliseconds(950);
    private static readonly TimeSpan PlayDelay = TimeSpan.FromMilliseconds(620);
    private static readonly TimeSpan PlayDuration = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan GlintDelay = TimeSpan.FromMilliseconds(1020);
    private static readonly TimeSpan GlintDuration = TimeSpan.FromMilliseconds(700);
    private const float GlintLength = 0.07f;

    // The loading loop, as granary-mark-loading.svg: a stretch of 28 % of the ring, once through in 1.4 s.
    private static readonly TimeSpan LoopDuration = TimeSpan.FromMilliseconds(1400);
    private const float RunLength = 0.28f;

    // The triangle's pivot, 40 % across and half down it, and the pale grey of the SVG's play and track.
    private static readonly Vector3 PlayPivot = new(90, 88, 0);
    private static readonly Color Pale = Color.FromArgb(0xFF, 0xE8, 0xE8, 0xEC);

    private readonly bool _animates = new UISettings().AnimationsEnabled;
    private readonly Compositor _compositor;
    private readonly ContainerVisual _mark;
    private readonly ShapeVisual _ringVisual;
    private readonly ShapeVisual _glintVisual;
    private readonly CompositionEllipseGeometry _ring;
    private readonly CompositionEllipseGeometry _glint;
    private readonly CompositionSpriteShape _track;
    private readonly CompositionPropertySet _motion;
    private readonly Polygon _play;
    private readonly Visual _playVisual;

    public GranaryMark()
    {
        IsHitTestVisible = false;
        var host = new Grid();
        var pale = new SolidColorBrush(Pale);
        _play = new Polygon
        {
            Points = { new Point(74, 66), new Point(114, 88), new Point(74, 110) },
            Fill = pale,
            Stroke = pale,
            StrokeThickness = 10,
            StrokeLineJoin = PenLineJoin.Round,
        };
        Children.Add(host);
        Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = new Canvas { Width = Units, Height = Units, Children = { _play } } });

        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _mark = _compositor.CreateContainerVisual();
        _mark.Size = new Vector2(Units);
        _mark.CenterPoint = new Vector3(Middle, Middle, 0);

        var gradient = _compositor.CreateLinearGradientBrush();
        gradient.StartPoint = Vector2.Zero;
        gradient.EndPoint = Vector2.One;
        gradient.ColorStops.Add(_compositor.CreateColorGradientStop(0, Color.FromArgb(0xFF, 0xEF, 0x44, 0x44)));
        gradient.ColorStops.Add(_compositor.CreateColorGradientStop(1, Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B)));

        _ring = Circle();
        _track = Stroke(Circle(), _compositor.CreateColorBrush(Color.FromArgb(56, 0x8A, 0x8A, 0x96)), RingWidth);
        _ringVisual = _compositor.CreateShapeVisual();
        _ringVisual.Size = new Vector2(Units);
        _ringVisual.Shapes.Add(Stroke(_ring, gradient, RingWidth));
        _glint = Circle();
        _glintVisual = _compositor.CreateShapeVisual();
        _glintVisual.Size = new Vector2(Units);
        _glintVisual.Shapes.Add(Stroke(_glint, _compositor.CreateColorBrush(Color.FromArgb(0xFF, 0xFF, 0xF6, 0xE0)), 12));
        _mark.Children.InsertAtTop(_ringVisual);
        _mark.Children.InsertAtTop(_glintVisual);
        ElementCompositionPreview.SetElementChildVisual(host, _mark);

        _playVisual = ElementCompositionPreview.GetElementVisual(_play);
        ElementCompositionPreview.SetIsTranslationEnabled(_play, true);
        _motion = _compositor.CreatePropertySet();
        _motion.InsertScalar("Run", 0);
        _motion.InsertScalar("Glint", 0);

        SizeChanged += (_, e) => Fit(e.NewSize);
        // Loaded only starts the loop: an intro waiting for Loaded keeps the mark hidden until it plays.
        Loaded += (_, _) =>
        {
            if (IsLoading)
            {
                ShowLoading();
            }
        };
        Unloaded += (_, _) => ShowStill();
        ShowStill();
    }

    /// <summary>Runs the loading loop while true; the mark stands still otherwise.</summary>
    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    /// <summary>
    /// Plays the intro once; done when it has played, at once with Windows' animations off. Called before the mark shows,
    /// it keeps it hidden and starts once it has been drawn: animations of a visual not on screen yet end at once.
    /// </summary>
    public async Task PlayIntroAsync()
    {
        ShowStill();
        if (!_animates)
        {
            return;
        }
        _ringVisual.Opacity = 0;
        _playVisual.Opacity = 0;
        if (!IsLoaded)
        {
            var loaded = new TaskCompletionSource();
            void OnLoaded(object sender, RoutedEventArgs e) => loaded.TrySetResult();
            Loaded += OnLoaded;
            await loaded.Task;
            Loaded -= OnLoaded;
        }
        var drawn = new TaskCompletionSource();
        void OnRendering(object? sender, object e) => drawn.TrySetResult();
        CompositionTarget.Rendering += OnRendering;
        await drawn.Task;
        CompositionTarget.Rendering -= OnRendering;
        await PlayIntroNowAsync();
    }

    private Task PlayIntroNowAsync()
    {
        // The glint follows an expression, which never ends: it starts outside the batch that tells the intro is done.
        RunAlong(_glint, "Glint", GlintLength);
        var played = new TaskCompletionSource();
        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var draw = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.55f, 0), new Vector2(0.2f, 1));

        // The ring draws itself back from its upper end while turning a quarter into place.
        var trim = Scalar(DrawDuration);
        trim.InsertKeyFrame(0, ArcHigh);
        trim.InsertKeyFrame(1, ArcLow, draw);
        _ring.StartAnimation(nameof(CompositionGeometry.TrimStart), trim);
        var turn = Scalar(DrawDuration);
        turn.InsertKeyFrame(0, -40);
        turn.InsertKeyFrame(1, 0, draw);
        _mark.StartAnimation(nameof(Visual.RotationAngleInDegrees), turn);
        var appear = Scalar(DrawDuration);
        appear.InsertKeyFrame(0, 0);
        appear.InsertKeyFrame(0.06f, 1);
        _ringVisual.StartAnimation(nameof(Visual.Opacity), appear);

        // Play pops in from the left, a little too large at first.
        var playIn = Scalar(PlayDuration, PlayDelay);
        playIn.InsertKeyFrame(0, 0);
        playIn.InsertKeyFrame(0.4f, 1);
        _playVisual.StartAnimation(nameof(Visual.Opacity), playIn);
        var pop = Vector(PlayDuration, PlayDelay);
        pop.InsertKeyFrame(0, new Vector3(0.2f, 0.2f, 1));
        pop.InsertKeyFrame(0.6f, new Vector3(1.12f, 1.12f, 1));
        pop.InsertKeyFrame(1, Vector3.One);
        _playVisual.StartAnimation(nameof(Visual.Scale), pop);
        var slide = Vector(PlayDuration, PlayDelay);
        slide.InsertKeyFrame(0, new Vector3(-12, 0, 0));
        slide.InsertKeyFrame(1, Vector3.Zero, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0.6f), new Vector2(0.5f, 1)));
        _playVisual.StartAnimation("Translation", slide);

        // Then a glint runs once round the ring.
        var glint = Scalar(GlintDuration, GlintDelay);
        glint.InsertKeyFrame(0, 0);
        glint.InsertKeyFrame(1, 1 + GlintLength, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0), new Vector2(0.58f, 1)));
        _motion.StartAnimation("Glint", glint);
        var shine = Scalar(GlintDuration, GlintDelay);
        shine.InsertKeyFrame(0, 0);
        shine.InsertKeyFrame(0.15f, 0.55f);
        shine.InsertKeyFrame(0.85f, 0.55f);
        shine.InsertKeyFrame(1, 0);
        _glintVisual.StartAnimation(nameof(Visual.Opacity), shine);

        batch.End();
        batch.Completed += (_, _) => played.TrySetResult();
        return played.Task;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GranaryMarkAutomationPeer(this);

    // The ring's visual sits at the middle at the control's size, as the Viewbox puts play.
    private void Fit(Size size)
    {
        float scale = (float)Math.Min(size.Width, size.Height) / Units;
        _mark.Scale = new Vector3(scale, scale, 1);
        _mark.Offset = new Vector3((float)size.Width / 2 - Middle, (float)size.Height / 2 - Middle, 0);
    }

    private void ShowLoading()
    {
        if (!IsLoading || !IsLoaded)
        {
            ShowStill();
            return;
        }
        ShowStill();
        if (!_animates)
        {
            return;
        }
        _ringVisual.Shapes.Insert(0, _track);
        RunAlong(_ring, "Run", RunLength);
        var run = Scalar(LoopDuration);
        run.InsertKeyFrame(0, 0);
        run.InsertKeyFrame(1, 1 + RunLength, _compositor.CreateLinearEasingFunction());
        run.IterationBehavior = AnimationIterationBehavior.Forever;
        _motion.StartAnimation("Run", run);

        var breathe = Vector(LoopDuration);
        breathe.InsertKeyFrame(0.5f, new Vector3(0.88f, 0.88f, 1));
        breathe.InsertKeyFrame(1, Vector3.One);
        breathe.IterationBehavior = AnimationIterationBehavior.Forever;
        _playVisual.StartAnimation(nameof(Visual.Scale), breathe);
        var dim = Scalar(LoopDuration);
        dim.InsertKeyFrame(0.5f, 0.7f);
        dim.InsertKeyFrame(1, 1);
        dim.IterationBehavior = AnimationIterationBehavior.Forever;
        _playVisual.StartAnimation(nameof(Visual.Opacity), dim);
    }

    // The mark at rest: the whole G, play in place, no glint.
    private void ShowStill()
    {
        foreach (var geometry in new[] { _ring, _glint })
        {
            geometry.StopAnimation(nameof(CompositionGeometry.TrimStart));
            geometry.StopAnimation(nameof(CompositionGeometry.TrimEnd));
        }
        _ring.TrimStart = ArcLow;
        _ring.TrimEnd = ArcHigh;
        _ringVisual.Shapes.Remove(_track);
        _motion.StopAnimation("Run");
        _motion.StopAnimation("Glint");
        _mark.StopAnimation(nameof(Visual.RotationAngleInDegrees));
        _mark.RotationAngleInDegrees = 0;
        _ringVisual.StopAnimation(nameof(Visual.Opacity));
        _ringVisual.Opacity = 1;
        _glintVisual.StopAnimation(nameof(Visual.Opacity));
        _glintVisual.Opacity = 0;
        _playVisual.StopAnimation(nameof(Visual.Opacity));
        _playVisual.StopAnimation(nameof(Visual.Scale));
        _playVisual.StopAnimation("Translation");
        _playVisual.Opacity = 1;
        _playVisual.CenterPoint = PlayPivot;
        _playVisual.Scale = Vector3.One;
        _playVisual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    // A stretch of the ring, the given share of it long, that moves along it as the property runs from 0 to 1 + length:
    // in at the G's upper end, out at its lower one.
    private void RunAlong(CompositionEllipseGeometry geometry, string property, float length)
    {
        foreach (var (end, lag) in new[] { (nameof(CompositionGeometry.TrimEnd), length), (nameof(CompositionGeometry.TrimStart), 0f) })
        {
            var along = _compositor.CreateExpressionAnimation($"Clamp(High - (m.{property} - Lag) * (High - Low), Low, High)");
            along.SetReferenceParameter("m", _motion);
            along.SetScalarParameter("High", ArcHigh);
            along.SetScalarParameter("Low", ArcLow);
            along.SetScalarParameter("Lag", lag);
            geometry.StartAnimation(end, along);
        }
    }

    private CompositionEllipseGeometry Circle()
    {
        var circle = _compositor.CreateEllipseGeometry();
        circle.Center = new Vector2(Middle);
        circle.Radius = new Vector2(Radius);
        circle.TrimOffset = ArcStart;
        circle.TrimStart = ArcLow;
        circle.TrimEnd = ArcHigh;
        return circle;
    }

    private CompositionSpriteShape Stroke(CompositionGeometry geometry, CompositionBrush brush, float width)
    {
        var shape = _compositor.CreateSpriteShape(geometry);
        shape.StrokeBrush = brush;
        shape.StrokeThickness = width;
        shape.StrokeStartCap = CompositionStrokeCap.Round;
        shape.StrokeEndCap = CompositionStrokeCap.Round;
        return shape;
    }

    private ScalarKeyFrameAnimation Scalar(TimeSpan duration, TimeSpan delay = default)
    {
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = duration;
        animation.DelayTime = delay;
        animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        return animation;
    }

    private Vector3KeyFrameAnimation Vector(TimeSpan duration, TimeSpan delay = default)
    {
        var animation = _compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = duration;
        animation.DelayTime = delay;
        animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        return animation;
    }

    // Screen readers hear a progress indicator while it loads (named by the page, "Loading"), else a picture.
    private sealed partial class GranaryMarkAutomationPeer(GranaryMark owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() =>
            ((GranaryMark)Owner).IsLoading ? AutomationControlType.ProgressBar : AutomationControlType.Image;

        protected override string GetClassNameCore() => nameof(GranaryMark);

        protected override bool IsControlElementCore() => true;
    }
}
