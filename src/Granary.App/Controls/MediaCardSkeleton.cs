using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Granary.App.Controls;

/// <summary>
/// The place of a poster card whose title has not arrived yet, as the web's skeleton: a 2:3 box and a title bar three
/// quarters wide, pulsing slowly. Screen readers skip it.
/// </summary>
public sealed partial class MediaCardSkeleton : Grid
{
    private static readonly TimeSpan PulseDuration = TimeSpan.FromSeconds(2);
    private const double TitleGap = 8;

    private readonly Border _art;
    private readonly Border _title;

    public MediaCardSkeleton()
    {
        var fill = (Brush)Application.Current.Resources["SiloSurfaceRaisedBrush"];
        VerticalAlignment = VerticalAlignment.Top;
        IsHitTestVisible = false;
        RowSpacing = TitleGap;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _art = new Border { Background = fill, CornerRadius = (CornerRadius)Application.Current.Resources["SiloCardCornerRadius"] };
        _title = new Border { Background = fill, Height = 16, CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left };
        SetRow(_title, 1);
        Children.Add(_art);
        Children.Add(_title);
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        Loaded += (_, _) => Pulse();
    }

    // Sized by the width it is measured at, as MediaCardView: sized from SizeChanged, a skeleton the repeater parked at 0×0
    // or laid out at another width changed its height after every arrange, and a page of them never settled (a layout
    // cycle while paging a wide grid).
    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsFinite(availableSize.Width) && availableSize.Width > 0)
        {
            _art.Height = availableSize.Width * 3 / 2;
            _title.Width = availableSize.Width * 3 / 4;
        }
        return base.MeasureOverride(availableSize);
    }

    /// <summary>The title bar shows: off while the profile has nothing written under posters, as the web's skeleton.</summary>
    public bool ShowsTitle
    {
        get => _title.Visibility == Visibility.Visible;
        set
        {
            _title.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            RowSpacing = value ? TitleGap : 0;
        }
    }

    // On the compositor, so a screen of skeletons costs the UI thread nothing.
    private void Pulse()
    {
        var visual = ElementCompositionPreview.GetElementVisual(this);
        var pulse = visual.Compositor.CreateScalarKeyFrameAnimation();
        pulse.InsertKeyFrame(0f, 1f);
        pulse.InsertKeyFrame(0.5f, 0.5f);
        pulse.InsertKeyFrame(1f, 1f);
        pulse.Duration = PulseDuration;
        pulse.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Opacity", pulse);
    }
}
