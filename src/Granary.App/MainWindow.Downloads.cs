using System.ComponentModel;
using System.Numerics;
using Granary.Core.ViewModels.Downloads;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace Granary.App;

// The sidebar's Downloads entry (DownloadActivity): its count, bound in MainWindow.xaml, and while a download comes the dot
// beside it, which breathes slowly on the compositor, so it costs the UI thread nothing: down to a third and back every three
// seconds, slower and fainter than the activity indicator's count. With Windows' "Animation effects" off it stands still. The
// window lives as long as the app; it lets go of Windows' settings as it closes. The notice of downloads that finished or
// failed (DownloadNotices) is bound in MainWindow.xaml.
public sealed partial class MainWindow
{
    private const float PulseDimmed = 0.3f;
    private static readonly TimeSpan PulseBreath = TimeSpan.FromSeconds(3);

    private readonly UISettings _windowsSettings = new();
    private bool _pulsing;

    /// <summary>The sidebar's count of downloads coming or waiting, and whether one comes now.</summary>
    public DownloadActivity DownloadsActivity { get; }

    /// <summary>The notice in the window of downloads that finished or failed while Granary is in front.</summary>
    public DownloadNotices DownloadNotices { get; }

    private void InitializeDownloads()
    {
        DownloadsActivity.PropertyChanged += OnDownloadsActivityChanged;
        _windowsSettings.AnimationsEnabledChanged += OnAnimationsChanged;
        DownloadsPulse.Loaded += (_, _) => ShowPulse();
        DownloadsPulse.Unloaded += (_, _) => ShowPulse();
        Closed += (_, _) =>
        {
            DownloadsActivity.PropertyChanged -= OnDownloadsActivityChanged;
            _windowsSettings.AnimationsEnabledChanged -= OnAnimationsChanged;
        };
    }

    private void OnDownloadsActivityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadActivity.IsTransferring))
        {
            ShowPulse();
        }
    }

    // Raised off the UI thread.
    private void OnAnimationsChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) => DispatcherQueue.TryEnqueue(ShowPulse);

    private void ShowPulse()
    {
        bool pulse = _windowsSettings.AnimationsEnabled && DownloadsPulse.IsLoaded && DownloadsActivity.IsTransferring;
        if (pulse == _pulsing)
        {
            return;
        }
        _pulsing = pulse;
        var visual = ElementCompositionPreview.GetElementVisual(DownloadsPulse);
        if (!pulse)
        {
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.Opacity = 1;
            return;
        }
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0), new Vector2(0.58f, 1));
        var breath = compositor.CreateScalarKeyFrameAnimation();
        breath.InsertKeyFrame(0, 1);
        breath.InsertKeyFrame(0.5f, PulseDimmed, ease);
        breath.InsertKeyFrame(1, 1, ease);
        breath.Duration = PulseBreath;
        breath.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation(nameof(Visual.Opacity), breath);
    }
}
