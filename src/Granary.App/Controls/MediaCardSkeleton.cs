using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Granary.App.Controls;

/// <summary>
/// The place of a poster card whose title has not arrived yet, as the web's skeleton: a 2:3 box and a title bar three
/// quarters wide, pulsing slowly. Screen readers skip it.
/// </summary>
public sealed partial class MediaCardSkeleton : Grid
{
    private static readonly TimeSpan PulseDuration = TimeSpan.FromSeconds(2);

    private readonly Border _art;
    private readonly Border _title;

    public MediaCardSkeleton()
    {
        var fill = (Brush)Application.Current.Resources["SiloSurfaceRaisedBrush"];
        VerticalAlignment = VerticalAlignment.Top;
        IsHitTestVisible = false;
        RowSpacing = 8;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _art = new Border { Background = fill, CornerRadius = (CornerRadius)Application.Current.Resources["SiloCardCornerRadius"] };
        _title = new Border { Background = fill, Height = 16, CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left };
        SetRow(_title, 1);
        Children.Add(_art);
        Children.Add(_title);
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        SizeChanged += (_, e) =>
        {
            _art.Height = e.NewSize.Width * 3 / 2;
            _title.Width = e.NewSize.Width * 3 / 4;
        };
        Loaded += (_, _) => Pulse();
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
