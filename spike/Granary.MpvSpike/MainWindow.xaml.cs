using Granary.MpvSpike.Surfaces;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace Granary.MpvSpike;

public sealed partial class MainWindow : Window
{
    private readonly SpikeOptions _options;
    private readonly nint _hwnd;
    private IVideoSurface? _surface;
    private DispatcherQueueTimer? _statusTimer;
    private bool _updatingSeekBar;
    private double _duration;

    internal MpvPlayer? Player { get; private set; }

    internal string ModeTag => _options.Mode switch
    {
        SurfaceMode.RenderApi => "render",
        SurfaceMode.Hwnd => _options.HwndBottom ? "hwnd-bottom" : "hwnd",
        _ => "composition",
    };

    internal string SurfaceDescription => _surface?.Describe() ?? "<none>";

    internal SpikeOptions Options => _options;

    internal MainWindow(SpikeOptions options)
    {
        _options = options;
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Title = $"Granary mpv spike – {ModeTag}";
        AppWindow.Resize(new SizeInt32(1600, 900));
        Closed += OnClosed;
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        SpikeLog.Open(_options.LogPath);
        SpikeLog.Write("app", $"mode={ModeTag} libmpv api=0x{Interop.LibMpv.ClientApiVersion():X} source={(_options.Source is null ? "<none>" : Redacted(_options.Source))} header={(_options.Header is null ? "none" : "set")}");
        DisplayInfo.LogOutputs();

        VideoPanel.Visibility = _options.Mode == SurfaceMode.Hwnd ? Visibility.Collapsed : Visibility.Visible;
        _surface = _options.Mode switch
        {
            SurfaceMode.RenderApi => new RenderApiSurface(VideoPanel, _options.HdrOutput),
            SurfaceMode.Hwnd => new HwndSurface(_hwnd, VideoHost, _options.HwndBottom),
            _ => new CompositionSurface(VideoPanel, DispatcherQueue, _options.HdrOutput),
        };

        Player = new MpvPlayer();
        _surface.ConfigureBeforeInit(Player);
        Player.SetOption("hwdec", _options.Hwdec);
        Player.SetOption("keep-open", "yes");
        Player.SetOption("idle", "yes");
        Player.SetOption("input-default-bindings", "no");
        Player.SetOption("input-vo-keyboard", "no");
        Player.SetOption("osc", "no");
        Player.SetOption("ytdl", "no");
        Player.SetOption("sub-auto", "no");
        Player.SetOption("rebase-start-time", "yes");
        if (_options.Header is not null)
        {
            Player.SetOption("http-header-fields", _options.Header);
        }
        if (_options.SmallCache)
        {
            // Forces new range requests on seeks, so header changes become observable.
            Player.SetOption("demuxer-max-bytes", "256KiB");
            Player.SetOption("demuxer-max-back-bytes", "0");
            Player.SetOption("demuxer-readahead-secs", "1");
        }
        Player.PropertyChanged += OnPlayerPropertyChanged;
        Player.EventRaised += (id, info) => SpikeLog.Write("event", $"{id} {info}");
        Player.Initialize();
        foreach (string name in new[] { "mpv-version", "ffmpeg-version", "libass-version", "mpv-configuration" })
        {
            SpikeLog.Write("build", $"{name}={Player.GetString(name)}");
        }
        _surface.AttachAfterInit(Player);
        SpikeLog.Write("app", $"surface ready: {_surface.Name}");

        if (_options.Source is not null)
        {
            Player.Command("loadfile", _options.Source);
        }

        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(1);
        _statusTimer.Tick += (_, _) => Info.Text =
            $"{ModeTag} | {_surface.Describe()} | hwdec={Player.GetString("hwdec-current")} | drops={Player.GetString("frame-drop-count")} | XAML overlay visible";
        _statusTimer.Start();

        if (_options.SelfTest)
        {
            _ = new SelfTest(this, _options).RunAsync();
        }
    }

    private static string Redacted(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile ? $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}" : source;

    private void OnPlayerPropertyChanged(string name, object? value) => DispatcherQueue.TryEnqueue(() =>
    {
        switch (name)
        {
            case "time-pos" when value is double position:
                _updatingSeekBar = true;
                SeekBar.Value = Math.Min(position, SeekBar.Maximum);
                _updatingSeekBar = false;
                TimeText.Text = $"{Format(position)} / {Format(_duration)}";
                break;
            case "duration" when value is double duration:
                _duration = duration;
                SeekBar.Maximum = Math.Max(1, duration);
                break;
            case "pause" when value is bool paused:
                PlayPauseButton.Content = paused ? "Play" : "Pause";
                break;
            case "hwdec-current":
                SpikeLog.Write("prop", $"hwdec-current={value}");
                break;
        }
    });

    private static string Format(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"m\:ss");

    private void OnVideoAreaChanged(object sender, SizeChangedEventArgs e) => _surface?.UpdateLayout();

    private void OnCompositionScaleChanged(SwapChainPanel sender, object args) => _surface?.UpdateLayout();

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => Player?.Command("cycle", "pause");

    private void OnSeekBarValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_updatingSeekBar && Player is not null)
        {
            Player.Command("seek", e.NewValue.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute");
        }
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => SetFullscreen(AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen);

    private void OnPlayPauseAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Player?.Command("cycle", "pause");
        args.Handled = true;
    }

    private void OnFullscreenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SetFullscreen(AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen);
        args.Handled = true;
    }

    private void OnEscapeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SetFullscreen(false);
        args.Handled = true;
    }

    internal void SetFullscreen(bool fullscreen)
    {
        AppWindow.SetPresenter(fullscreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        SpikeLog.Write("app", $"fullscreen={fullscreen}");
    }

    internal void ResizeTo(int width, int height)
    {
        AppWindow.Resize(new SizeInt32(width, height));
        SpikeLog.Write("app", $"resize window to {width}x{height}");
    }

    internal async Task CaptureAsync(string path)
    {
        await ScreenCapture.SaveWindowAsync(_hwnd, path);
        SpikeLog.Write("shot", path);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _statusTimer?.Stop();
        _surface?.Dispose(); // the render context must go before the mpv handle
        Player?.Dispose();
        SpikeLog.Write("app", "closed");
        SpikeLog.Close();
    }
}
