using System.ComponentModel;
using System.Globalization;
using Granary.App.Controls;
using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Player;
using Granary.Player;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;

namespace Granary.App.Views;

/// <summary>
/// The player: the video fills the window and the controls float over it (docs/ui-design.md, Player).
/// Keyboard and clicks on the picture are in PlayerPage.Input.cs, the fading controls in PlayerPage.AutoHide.cs.
/// </summary>
public sealed partial class PlayerPage : Page
{
    public static readonly DependencyProperty IsFullScreenProperty =
        DependencyProperty.Register(nameof(IsFullScreen), typeof(bool), typeof(PlayerPage), new PropertyMetadata(false));

    public static readonly DependencyProperty IsMiniViewProperty =
        DependencyProperty.Register(nameof(IsMiniView), typeof(bool), typeof(PlayerPage), new PropertyMetadata(false));

    public static readonly DependencyProperty ShowsRemainingProperty =
        DependencyProperty.Register(nameof(ShowsRemaining), typeof(bool), typeof(PlayerPage), new PropertyMetadata(false));

    // MainWindow's title bar row. In a window the top bar starts below it: that strip is the drag area.
    private const double TitleBarHeight = 40;

    // The mini view's width in view pixels.
    private const double MiniViewWidth = 480;

    private AppWindow? _appWindow;
    private AppWindowPresenter? _windowedPresenter;
    private RectInt32? _windowedBounds;

    public PlayerPage()
    {
        // Made outside the container: a disposable transient from the root provider would be kept until the app exits.
        ViewModel = ActivatorUtilities.CreateInstance<PlayerViewModel>(App.Services);
        InitializeComponent();
        VideoHost.VideoOutput = App.Services.GetRequiredService<ICompositionVideoOutput>();
        InitializeInput();
        InitializeAutoHide();
        InitializeSkipPrompt();
        InitializeNextUp();
        InitializeSubtitlePanels();
        RegisterPropertyChangedCallback(IsMiniViewProperty, (_, _) =>
        {
            ShowNextUpScreen();
            ShowControls();
        });
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        LostFocus += (_, _) => KeepFocusInPage();
    }

    public PlayerViewModel ViewModel { get; }

    /// <summary>The window shows the full-screen presenter.</summary>
    public bool IsFullScreen
    {
        get => (bool)GetValue(IsFullScreenProperty);
        private set => SetValue(IsFullScreenProperty, value);
    }

    /// <summary>The window shows the compact overlay presenter: small and above the others.</summary>
    public bool IsMiniView
    {
        get => (bool)GetValue(IsMiniViewProperty);
        private set => SetValue(IsMiniViewProperty, value);
    }

    /// <summary>The end of the seek bar shows the time left instead of the duration.</summary>
    public bool ShowsRemaining
    {
        get => (bool)GetValue(ShowsRemainingProperty);
        private set => SetValue(ShowsRemainingProperty, value);
    }

    private bool HasTransport => ViewModel.Phase is PlayerPhase.Playing or PlayerPhase.Ended;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is PlayRequest request)
        {
            _ = ViewModel.StartAsync(request);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        SetFullScreen(false);
        SetMiniView(false);
        // Close stops playback, but Alt+Left, the mouse back button, Ctrl+K or a session change leave without it, and
        // the sound must not go on behind another page nor the server session stay open.
        ViewModel.Leave();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _appWindow = AppWindow.GetFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        _appWindow.Changed += OnAppWindowChanged;
        IsFullScreen = _appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        IsMiniView = _appWindow.Presenter.Kind == AppWindowPresenterKind.CompactOverlay;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ShowSkipIntervals();
        Focus(FocusState.Programmatic);
        ShowControls();
        // StartAsync runs on navigation, before this: it can have failed already.
        if (ViewModel.IsFailed)
        {
            OnFailed();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_appWindow is not null)
        {
            _appWindow.Changed -= OnAppWindowChanged;
        }
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        StopInput();
        StopAutoHide();
        StopSubtitlePanels();
        ViewModel.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.Phase):
                ShowSkipIntervals();
                ShowControls();
                FadeMiniPlayer();
                if (ViewModel.IsFailed)
                {
                    OnFailed();
                }
                else
                {
                    KeepFocusInPage();
                }
                break;
            case nameof(PlayerViewModel.IsPaused) when ViewModel.IsPaused:
                ShowControls();
                break;
            case nameof(PlayerViewModel.SkipBackSeconds) or nameof(PlayerViewModel.SkipForwardSeconds):
                ShowSkipIntervals();
                break;
            case nameof(PlayerViewModel.IsNextUpScreenShown):
                ShowNextUpScreen();
                break;
            case nameof(PlayerViewModel.NextUpStill):
                ShowNextUpStill();
                break;
            case nameof(PlayerViewModel.HasSkipPrompt) when ViewModel.HasSkipPrompt:
                AnnounceSkipCaption();
                break;
            case nameof(PlayerViewModel.HasSkipPrompt):
                // A pill that had the keyboard is gone.
                KeepFocusInPage();
                break;
        }
    }

    // Keyboard users land on what they can do next; screen readers read the message.
    private void OnFailed() => DispatcherQueue.TryEnqueue(() =>
    {
        Button target = ViewModel.CanRetry ? RetryButton : FailedBackButton;
        target.Focus(FocusState.Programmatic);
        FrameworkElementAutomationPeer.FromElement(ErrorText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    });

    // The keyboard map lives on the page, so focus must stay in it. It can still drop out, for one when a focused
    // button collapses with its panel (Retry, the transport); the page takes it back, unless a menu has it.
    private void KeepFocusInPage() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!IsLoaded || XamlRoot is not { } root || HasOpenMenu())
        {
            return;
        }
        if (FocusManager.GetFocusedElement(root) is not DependencyObject focused || !IsWithin(focused, this) || !IsShown(focused))
        {
            Focus(FocusState.Programmatic);
        }
    });

    private static bool IsShown(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: Visibility.Collapsed })
            {
                return false;
            }
        }
        return true;
    }

    // The intervals come from the profile's settings, which arrive with the title.
    private void ShowSkipIntervals()
    {
        string back = ViewModel.SkipBackSeconds.ToString("0", CultureInfo.InvariantCulture);
        string forward = ViewModel.SkipForwardSeconds.ToString("0", CultureInfo.InvariantCulture);
        SkipBackText.Text = back;
        SkipForwardText.Text = forward;
        AutomationProperties.SetName(SkipBackButton, $"Back {back} seconds");
        AutomationProperties.SetName(SkipForwardButton, $"Forward {forward} seconds");
        ToolTipService.SetToolTip(SkipBackButton, $"Back {back} seconds (Left arrow)");
        ToolTipService.SetToolTip(SkipForwardButton, $"Forward {forward} seconds (Right arrow)");
        SeekBar.BackStepSeconds = ViewModel.SkipBackSeconds;
        SeekBar.ForwardStepSeconds = ViewModel.SkipForwardSeconds;
    }

    private void OnSeekRequested(SeekBar sender, double seconds) => ViewModel.SeekTo(seconds);

    // As the web's 1fr auto 1fr row: the right column is never narrower than its buttons. In a narrow window the
    // transport moves left, rather than the buttons being cut off.
    private void OnRightControlsLayoutUpdated(object? sender, object e)
    {
        var shown = RightControls.Children.OfType<FrameworkElement>().Where(child => child.Visibility == Visibility.Visible).ToList();
        double width = shown.Sum(child => child.DesiredSize.Width) + Math.Max(0, shown.Count - 1) * RightControls.Spacing;
        if (Math.Abs(RightColumn.MinWidth - width) > 0.5)
        {
            RightColumn.MinWidth = width;
        }
    }

    private void OnEndTimeClick(object sender, RoutedEventArgs e) => ShowsRemaining = !ShowsRemaining;

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OnMiniViewClick(object sender, RoutedEventArgs e) => SetMiniView(!IsMiniViewNow);

    // In pixels: the playback info shows the area the picture is drawn into.
    private void OnVideoHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        ViewModel.SetPictureSize((int)Math.Round(e.NewSize.Width * scale), (int)Math.Round(e.NewSize.Height * scale));
    }

    private void OnSpeedPicked(SpeedTrack sender, double speed) => ViewModel.SetSpeed(speed);

    // A quality or a file is a choice made: the menu closes, as the web player's does.
    private void OnQualityClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string preference })
        {
            SettingsMenu.Hide();
            ViewModel.SelectQuality(preference);
        }
    }

    private void OnVersionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string fileId })
        {
            SettingsMenu.Hide();
            ViewModel.SelectVersion(fileId);
        }
    }

    // "Playback settings, 1.5×" while the speed is not 1×.
    public string SettingsName(string speedBadge) => speedBadge.Length > 0 ? $"Playback settings, {speedBadge}" : "Playback settings";

    // A menu opens on the track that plays, so the keyboard starts from there.
    private void OnAudioMenuOpened(object? sender, object e) =>
        FocusRow(AudioRows, ViewModel.AudioTracks.ToList().FindIndex(option => option.IsSelected));

    private void OnSubtitleMenuOpened(object? sender, object e) =>
        FocusRow(SubtitleRows, ViewModel.SubtitleTracks.ToList().FindIndex(option => option.IsSelected));

    private void OnChaptersMenuOpened(object? sender, object e) =>
        FocusRow(ChapterRows, Math.Max(0, ViewModel.Chapters.ToList().FindIndex(option => option.IsCurrent)));

    private void OnChapterClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChapterOption option })
        {
            ChaptersMenu.Hide();
            ViewModel.SeekToChapter(option.StartSeconds);
        }
    }

    private static void FocusRow(ItemsControl rows, int index)
    {
        if (index >= 0 && rows.ContainerFromIndex(index) is ContentPresenter { Content: not null } row
            && VisualTreeHelper.GetChildrenCount(row) > 0 && VisualTreeHelper.GetChild(row, 0) is Button button)
        {
            button.Focus(FocusState.Programmatic);
        }
    }

    private const int SubtitleDelayStepMs = 100;

    private void OnSubtitleEarlierClick(object sender, RoutedEventArgs e) => ViewModel.ChangeSubtitleDelay(-SubtitleDelayStepMs);

    private void OnSubtitleLaterClick(object sender, RoutedEventArgs e) => ViewModel.ChangeSubtitleDelay(SubtitleDelayStepMs);

    private void OnSubtitleDelayResetClick(object sender, RoutedEventArgs e) => ViewModel.ResetSubtitleDelay();

    private void OnSubtitleTrackClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SubtitleTrackOption option })
        {
            SubtitleMenu.Hide();
            ViewModel.SelectSubtitle(option.TrackId);
        }
    }

    private void OnAudioTrackClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AudioTrackOption option })
        {
            AudioMenu.Hide();
            ViewModel.SelectAudio(option.Ordinal);
        }
    }

    // Read from the window, not from IsFullScreen, which follows the window's Changed event a moment later.
    private bool IsFullScreenNow => _appWindow?.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    private void ToggleFullScreen() => SetFullScreen(!IsFullScreenNow);

    private bool IsMiniViewNow => _appWindow?.Presenter.Kind == AppWindowPresenterKind.CompactOverlay;

    private void SetFullScreen(bool fullScreen)
    {
        if (_appWindow is not { } window || fullScreen == IsFullScreenNow)
        {
            return;
        }
        if (fullScreen)
        {
            // From the mini view to full screen, and back from there to the window it came from.
            SetMiniView(false);
            KeepWindowed(window);
            window.SetPresenter(AppWindowPresenterKind.FullScreen);
            return;
        }
        RestoreWindowed(window);
    }

    // The web player's Picture in Picture: the window itself, small and above the others (CompactOverlay).
    private void SetMiniView(bool mini)
    {
        if (_appWindow is not { } window || mini == IsMiniViewNow)
        {
            return;
        }
        if (mini)
        {
            SetFullScreen(false);
            KeepWindowed(window);
            var presenter = CompactOverlayPresenter.Create();
            presenter.InitialSize = CompactOverlaySize.Medium;
            window.SetPresenter(presenter);
            // 16:9, as the web's picture-in-picture window starts; the medium size alone keeps the window's shape.
            double scale = XamlRoot?.RasterizationScale ?? 1;
            window.Resize(new SizeInt32((int)Math.Round(MiniViewWidth * scale), (int)Math.Round(MiniViewWidth * 9 / 16 * scale)));
            return;
        }
        RestoreWindowed(window);
    }

    // Kept to return to: a new overlapped presenter would forget that the window was maximized, and the old one
    // alone puts a restored window back a little off its place.
    private void KeepWindowed(AppWindow window)
    {
        _windowedPresenter = window.Presenter;
        _windowedBounds = new RectInt32(window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height);
    }

    private void RestoreWindowed(AppWindow window)
    {
        if (_windowedPresenter is { } presenter)
        {
            window.SetPresenter(presenter);
        }
        else
        {
            window.SetPresenter(AppWindowPresenterKind.Overlapped);
        }
        if (window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored } && _windowedBounds is { } bounds)
        {
            window.MoveAndResize(bounds);
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange)
        {
            IsFullScreen = sender.Presenter.Kind == AppWindowPresenterKind.FullScreen;
            IsMiniView = sender.Presenter.Kind == AppWindowPresenterKind.CompactOverlay;
        }
    }

    // Instance methods because x:Bind function bindings call them on the page.
    // Segoe Fluent Icons: PlaySolid, PauseBold; Volume1 to Volume3, Mute; FullScreen, BackToWindow.
    public string PlayPauseGlyph(bool isPaused) => isPaused ? "" : "";

    // The play triangle's weight sits left of its box; nudged so it looks centred in the disc.
    public Thickness PlayPauseGlyphMargin(bool isPaused) => isPaused ? new Thickness(3, 0, 0, 0) : new Thickness(0);

    public string PlayPauseName(bool isPaused) => isPaused ? "Play" : "Pause";

    public string PlayPauseToolTip(bool isPaused) => $"{PlayPauseName(isPaused)} (Space)";

    public string VolumeGlyph(bool isMuted, double volume) => isMuted || volume <= 0 ? "" : volume switch
    {
        < 34 => "",
        < 67 => "",
        _ => "",
    };

    public string MuteName(bool isMuted) => isMuted ? "Unmute" : "Mute";

    public string MuteToolTip(bool isMuted) => $"{MuteName(isMuted)} (M)";

    public double VolumeOpacity(bool isMuted) => isMuted ? 0.45 : 1;

    public string FullScreenGlyph(bool isFullScreen) => isFullScreen ? "" : "";

    public string FullScreenName(bool isFullScreen) => isFullScreen ? "Exit full screen" : "Full screen";

    public string FullScreenToolTip(bool isFullScreen) => isFullScreen ? "Exit full screen (F or Esc)" : "Full screen (F)";

    public Thickness TopBarPadding(bool isFullScreen) => new(24, isFullScreen ? 24 : TitleBarHeight + 8, 24, 40);

    // Under Exit, and clear of the bottom bar, where it scrolls.
    public Thickness PlaybackInfoMargin(bool isFullScreen) => new(16, (isFullScreen ? 24 : TitleBarHeight + 8) + 48, 0, 160);

    // A utility button that is on is white; off, the rail's 78 %.
    public Brush UtilityForeground(bool isOn) => new SolidColorBrush(isOn ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(0xC7, 0xFF, 0xFF, 0xFF));

    public string OnStatus(bool isOn) => isOn ? "On" : "";

    // What the mini view leaves out: the full controls, the pills, the playback info and the Playing Next screen.
    public Visibility ShowsInWindow(bool shows, bool isMiniView) => shows && !isMiniView ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShowsTopChrome(bool isNextUpScreenShown, bool isMiniView) => ShowsInWindow(!isNextUpScreenShown, isMiniView);

    // "31:55 / 48:41", or "31:55 / -16:46" with the time left.
    public string TimesText(double position, double duration, bool showsRemaining) =>
        PlayerTime.Format(position, duration) + " / "
        + (showsRemaining ? PlayerTime.Remaining(position, duration) : PlayerTime.Format(duration));

    // Upper case on screen only: screen readers get the line as written, so "S1 · E1" is not spelled out.
    public string MetaText(string subtitle) => subtitle.ToUpper(CultureInfo.CurrentCulture);

    // Named for what it does; screen readers hear the times from the seek bar ("4 minutes of 1 hour 30 minutes").
    public string EndTimeName(bool showsRemaining) => showsRemaining ? "Show the duration" : "Show the time left";

    public Visibility ShowsTransport(PlayerPhase phase, bool isNextUpScreenShown, bool isMiniView) =>
        ShowsInWindow(phase is PlayerPhase.Playing or PlayerPhase.Ended && !isNextUpScreenShown, isMiniView);

    public bool ShowsSpinner(PlayerPhase phase, bool isBuffering) =>
        phase == PlayerPhase.Preparing || (phase == PlayerPhase.Playing && isBuffering);

    public Visibility SpinnerVisibility(PlayerPhase phase, bool isBuffering) =>
        ShowsSpinner(phase, isBuffering) ? Visibility.Visible : Visibility.Collapsed;

    public string SpinnerName(PlayerPhase phase) => phase == PlayerPhase.Preparing ? "Loading" : "Buffering";

    public Visibility Shows(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
}
