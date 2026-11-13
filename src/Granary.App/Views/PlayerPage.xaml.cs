using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Granary.App.Controls;
using Granary.App.Services;
using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Core.Texts;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Player;
using Granary.Player;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

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

    // MainWindow's title bar row. In a window the top bar starts below it: that strip is the drag area.
    private const double TitleBarHeight = 32;

    private AppWindow? _appWindow;
    // Previous episode, Play/Pause and Next episode in the taskbar preview, for as long as Windows' media controls.
    private PlayerTaskbarToolbar? _taskbarToolbar;
    // Windows' media controls and keys, from the page's load on (a page never loaded never played) until it is left.
    private PlayerSystemMedia? _systemMedia;
    // Discord's status, likewise, while Settings allows it.
    private PlayerRichPresence? _richPresence;
    // The display kept on while a title plays, likewise; the bar and the mini view are this page too.
    private PlayerKeepAwake? _keepAwake;
    // The window's full screen and mini view, the app's too: the player toggles them, and the window keeps them.
    private readonly WindowModeService _modes;
    // Whether a title opens the player in full screen, as it was last left on this PC.
    private readonly PlayerFullScreen _fullScreen;
    private readonly IPlaybackDevicePreferences _devicePreferences;

    public PlayerPage()
    {
        // Made outside the container: a disposable transient from the root provider would be kept until the app exits.
        ViewModel = ActivatorUtilities.CreateInstance<PlayerViewModel>(App.Services);
        _modes = App.Services.GetRequiredService<WindowModeService>();
        _fullScreen = App.Services.GetRequiredService<PlayerFullScreen>();
        _devicePreferences = App.Services.GetRequiredService<IPlaybackDevicePreferences>();
        InitializeComponent();
        AutomationProperties.SetName(this, AppText.Get("granary.player.pageName"));
        VideoHost.VideoOutput = App.Services.GetRequiredService<ICompositionVideoOutput>();
        InitializeInput();
        InitializeAutoHide();
        InitializeSkipPrompt();
        InitializePlanChange();
        InitializeSubtitlePanels();
        InitializeDock();
        InitializeNotices();
        InitializePartyPanel();
        ArrangeBarButtons();
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

    /// <summary>The window is full screen (<see cref="WindowModeService"/>).</summary>
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

    private bool HasTransport => ViewModel.Phase is PlayerPhase.Playing or PlayerPhase.Ended;

    /// <summary>
    /// Plays <paramref name="request"/>, a title opened from the app (a page, a Watch Party, <c>--play</c>); the window shows
    /// the page over everything first, and goes full screen when the player was in full screen as it was last left.
    /// </summary>
    public void Start(PlayRequest request)
    {
        if (_fullScreen.OpensInFullScreen)
        {
            _modes.SetFullScreen(true);
        }
        _ = ViewModel.StartAsync(request);
    }

    /// <summary>
    /// Ends playback as the window takes the player away: Exit, Stop, Alt+Left, the mouse back button, Ctrl+K or a session
    /// change; the sound must not go on behind another page nor the server session stay open. The mini view ends with
    /// it; full screen is the window's and stays as the viewer set it, and the next title opens as the player is left.
    /// </summary>
    public void Leave()
    {
        SetMiniView(false);
        // Saved as Granary goes on; closing it later waits for this PC's choices anyway.
        _ = _fullScreen.Left(_modes.IsViewersFullScreen, _isLeavingByEsc);
        ViewModel.Leave();
    }

    /// <summary>
    /// Ends playback as the window closes: the window stays as it is, the mini view included, and the next title opens as
    /// the player is left here (the mini view counting as the full screen it goes back to). Hands back that choice's save,
    /// for the window to wait for with the stops.
    /// </summary>
    public Task LeaveWithWindow()
    {
        var save = _fullScreen.Left(_modes.IsViewersFullScreen, byEsc: false);
        ViewModel.Leave();
        return save;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _appWindow = AppWindow.GetFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        _modes.Changed += OnWindowModeChanged;
        ShowWindowMode();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _taskbarToolbar ??= new PlayerTaskbarToolbar(ViewModel, App.Services.GetRequiredService<ITaskbarToolbar>(),
            App.Services.GetRequiredService<IUiDispatcher>());
        _systemMedia ??= new PlayerSystemMedia(ViewModel, App.Services.GetRequiredService<ISystemMediaControls>(),
            App.Services.GetRequiredService<IUiDispatcher>(), TimeProvider.System);
        _richPresence ??= new PlayerRichPresence(ViewModel, App.Services.GetRequiredService<IRichPresence>(), _devicePreferences,
            App.Services.GetRequiredService<TmdbTitleLookup>(), App.Services.GetRequiredService<IUiDispatcher>(), TimeProvider.System);
        _keepAwake ??= new PlayerKeepAwake(ViewModel, App.Services.GetRequiredService<IKeepAwake>());
        ShowSkipIntervals();
        QueueNextUpStill();
        Focus(FocusState.Programmatic);
        StartAutoHide();
        ShowControls();
        // StartAsync runs on navigation, before this: it can have failed already.
        if (ViewModel.IsFailed)
        {
            OnFailed();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _modes.Changed -= OnWindowModeChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        StopInput();
        StopAutoHide();
        StopNextUpStill();
        StopSubtitlePanels();
        StopPartyPanel();
        _taskbarToolbar?.Dispose();
        _taskbarToolbar = null;
        _systemMedia?.Dispose();
        _systemMedia = null;
        _richPresence?.Dispose();
        _richPresence = null;
        _keepAwake?.Dispose();
        _keepAwake = null;
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
            case nameof(PlayerViewModel.NextUpStill) or nameof(PlayerViewModel.NextUpBlursStill):
                QueueNextUpStill();
                break;
            case nameof(PlayerViewModel.HasSkipPrompt) when ViewModel.HasSkipPrompt:
                AnnounceSkipCaption();
                FocusPromptSoon();
                break;
            case nameof(PlayerViewModel.IsNextUpCardShown) when ViewModel.IsNextUpCardShown:
                FocusPromptSoon();
                break;
            case nameof(PlayerViewModel.Bar):
                ShowBar();
                break;
            case nameof(PlayerViewModel.HasSkipPrompt):
            case nameof(PlayerViewModel.IsNextUpCardShown):
            case nameof(PlayerViewModel.HasSyncNotice) when !ViewModel.HasSyncNotice:
                // A pill, the next episode's card or the sync card that had the keyboard is gone. A prompt that goes with
                // focus still on it gives it to the picture at once, before XAML could hand it on to a button of the
                // hidden bar.
                if (FocusedPrompt() is { } gone && !(IsWithin(gone, SkipPrompt) ? ViewModel.HasSkipPrompt : ViewModel.IsNextUpCardShown))
                {
                    Focus(FocusState.Programmatic);
                }
                KeepFocusInPage();
                break;
        }
    }

    // Keyboard users land on what they can do next; screen readers read the message. The bar has no room for it: the
    // full player comes back to say it.
    private void OnFailed() => DispatcherQueue.TryEnqueue(() =>
    {
        if (IsDocked)
        {
            ExpandRequested?.Invoke(this, EventArgs.Empty);
        }
        Button target = ViewModel.CanRetry ? RetryButton : FailedBackButton;
        target.Focus(FocusState.Programmatic);
        FrameworkElementAutomationPeer.FromElement(ErrorText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    });

    // The keyboard map lives on the page, so focus must stay in it. It can still drop out, for one when a focused
    // button collapses with its panel (Retry, the transport); the page takes it back, unless a menu has it. A panel
    // opened from a button gives focus back to it, so a remote goes on from there; shown after a key.
    private void KeepFocusInPage(Control? opener = null) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!IsLoaded || IsDocked || XamlRoot is not { } root || HasOpenMenu())
        {
            return;
        }
        if (FocusManager.GetFocusedElement(root) is DependencyObject focused && IsWithin(focused, this) && IsShown(focused))
        {
            return;
        }
        if (opener is not null && IsShown(opener) && opener.Focus(_lastInputWasKey ? FocusState.Keyboard : FocusState.Programmatic))
        {
            return;
        }
        Focus(FocusState.Programmatic);
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
        int backCount = (int)Math.Round(ViewModel.SkipBackSeconds);
        int forwardCount = (int)Math.Round(ViewModel.SkipForwardSeconds);
        AutomationProperties.SetName(SkipBackButton, AppText.Get("web.player.controls.skipBack", ("count", backCount)));
        AutomationProperties.SetName(SkipForwardButton, AppText.Get("web.player.controls.skipForward", ("count", forwardCount)));
        ToolTipService.SetToolTip(SkipBackButton, AppText.Get("granary.player.skipBackTooltip", ("count", backCount)));
        ToolTipService.SetToolTip(SkipForwardButton, AppText.Get("granary.player.skipForwardTooltip", ("count", forwardCount)));
        DockSkipBackText.Text = back;
        DockSkipForwardText.Text = forward;
        // The bar's own words, as the web's player bar has them.
        string dockBack = AppText.Get("web.player.bar.skipBackTitle", ("count", backCount));
        string dockForward = AppText.Get("web.player.bar.skipForwardTitle", ("count", forwardCount));
        AutomationProperties.SetName(DockSkipBackButton, dockBack);
        AutomationProperties.SetName(DockSkipForwardButton, dockForward);
        ToolTipService.SetToolTip(DockSkipBackButton, dockBack);
        ToolTipService.SetToolTip(DockSkipForwardButton, dockForward);
        SeekBar.BackStepSeconds = DockSeekBar.BackStepSeconds = ViewModel.SkipBackSeconds;
        SeekBar.ForwardStepSeconds = DockSeekBar.ForwardStepSeconds = ViewModel.SkipForwardSeconds;
    }

    private void OnSeekRequested(SeekBar sender, double seconds) => ViewModel.SeekTo(seconds);

    private void OnMarkerEdgeDragged(SeekBar sender, MarkerEdgeChange change) => ViewModel.MarkerEditor.SetEdge(change.Kind, change.Edge, change.Seconds);

    // The seek bar's handles while the markers are edited: the chosen kind's.
    public string? EditKindOf(bool isEditing, string activeKind) => isEditing ? activeKind : null;

    // As the web's 1fr auto 1fr row: the right column is never narrower than its buttons. In a narrow window the
    // transport moves left, rather than the buttons being cut off.
    private void OnRightControlsLayoutUpdated(object? sender, object e)
    {
        // Every layout pass of the window comes here: no allocations.
        double width = 0;
        int shown = 0;
        foreach (var child in RightControls.Children)
        {
            if (child.Visibility == Visibility.Visible)
            {
                width += child.DesiredSize.Width;
                shown++;
            }
        }
        width += Math.Max(0, shown - 1) * RightControls.Spacing;
        if (Math.Abs(RightColumn.MinWidth - width) > 0.5)
        {
            RightColumn.MinWidth = width;
        }
    }

    // Settings → Playback → Player buttons changed the bar: the buttons take its order, a menu goes with its button, and
    // focus that was on a button that went stays in the page.
    private void ShowBar()
    {
        ArrangeBarButtons();
        var bar = ViewModel.Bar;
        (bool Shown, FlyoutBase Menu)[] menus =
        [
            (bar.SleepTimer, SleepTimerMenu), (bar.Audio, AudioMenu), (bar.Subtitles, SubtitleMenu), (bar.Chapters, ChaptersMenu),
            (bar.Settings, SettingsMenu),
        ];
        foreach (var (shown, menu) in menus)
        {
            if (!shown)
            {
                menu.Hide();
            }
        }
        KeepFocusInPage();
    }

    // Each button's elements in the bar's order, moved only where that differs from the order shown. Tab and a remote's
    // arrows follow it.
    private void ArrangeBarButtons()
    {
        var children = RightControls.Children;
        int index = 0;
        foreach (var button in ViewModel.Bar.Order)
        {
            foreach (var element in ElementsOf(button))
            {
                int at = children.IndexOf(element);
                // Each button's elements are the bar's own children; one that is not stays out of the order.
                Debug.Assert(at >= 0, "A bar button's element is not in RightControls");
                if (at < 0)
                {
                    continue;
                }
                if (at != index)
                {
                    children.Move((uint)at, (uint)index);
                }
                index++;
            }
        }
    }

    private UIElement[] ElementsOf(PlayerButton button) => button switch
    {
        PlayerButton.Volume => [MuteButton, VolumeSlider],
        PlayerButton.SleepTimer => [SleepTimerSlot],
        PlayerButton.Audio => [AudioButton],
        PlayerButton.Subtitles => [SubtitleSlot],
        PlayerButton.Chapters => [ChaptersButton],
        PlayerButton.Settings => [SettingsButton],
        PlayerButton.EditMarkers => [EditMarkersSlot],
        PlayerButton.PlaybackInfo => [PlaybackInfoSlot],
        PlayerButton.MiniView => [MiniViewButton],
        PlayerButton.Fill => [FillSlot],
        _ => [FullScreenButton],
    };

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OnMiniViewClick(object sender, RoutedEventArgs e) => SetMiniView(!_modes.IsMiniView);

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

    // A menu opens on the track that plays, so the keyboard starts from there.
    private void OnAudioMenuOpened(object? sender, object e) =>
        FocusRow(AudioRows, ViewModel.AudioTracks.ToList().FindIndex(option => option.IsSelected));

    // Opening also reads the subtitles' sync again, so a job that ended while the menu was closed shows.
    private void OnSubtitleMenuOpened(object? sender, object e)
    {
        ViewModel.SubtitleMenuOpened();
        FocusRow(SubtitleRows, ViewModel.SubtitleTracks.ToList().FindIndex(option => option.IsSelected));
    }

    // The sleep timer's menu takes its rows as it opens and keeps them while it is open, as the title page's pickers do
    // (DetailHero): a timer that runs out, or a title that ends, would otherwise rebuild them under the focus and take it
    // out of the menu. A row picked after that does nothing harmful: Turn off finds the timer off already, and the end of
    // a title that has ended is refused.
    private void OnSleepTimerMenuOpening(object? sender, object e) => SleepTimerRows.ItemsSource = ViewModel.SleepTimerOptions;

    // It opens, while the timer is off, on the duration this device starts it on, so OK starts that; while it is on, on
    // Turn off, the first row.
    private void OnSleepTimerMenuOpened(object? sender, object e)
    {
        var options = SleepTimerRows.ItemsSource as IReadOnlyList<SleepTimerOption> ?? [];
        bool isOn = options.Count > 0 && options[0].Action == SleepTimerAction.TurnOff;
        // The rows were handed over as the menu opened: lay them out before looking for one.
        SleepTimerRows.UpdateLayout();
        FocusRow(SleepTimerRows, isOn ? 0 : Math.Max(0, options.ToList().FindIndex(option => option.IsDefault)));
    }

    private void OnSleepTimerOptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SleepTimerOption option })
        {
            SleepTimerMenu.Hide();
            ViewModel.ChooseSleepTimer(option);
        }
    }

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

    private void OnAudioEarlierClick(object sender, RoutedEventArgs e) => ViewModel.ChangeAudioDelay(-PlayerViewModel.AudioDelayStepMs);

    private void OnAudioLaterClick(object sender, RoutedEventArgs e) => ViewModel.ChangeAudioDelay(PlayerViewModel.AudioDelayStepMs);

    private void OnAudioDelayResetClick(object sender, RoutedEventArgs e) => ViewModel.ResetAudioDelay();

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

    // The window's full screen, the whole app's (F, F11, the button, a double click); from the mini view straight there.
    private void ToggleFullScreen() => _modes.ToggleFullScreen();

    // The web player's Picture in Picture: the window itself, small and above the others (CompactOverlay). Leaving it
    // returns to full screen or to the window, wherever it was opened from.
    private void SetMiniView(bool mini) => _modes.SetMiniView(mini);

    // A change after an Esc out of full screen is the viewer's choice for the next title.
    private void OnWindowModeChanged(object? sender, EventArgs e)
    {
        _fullScreen.ForgetEscape();
        ShowWindowMode();
    }

    private void ShowWindowMode()
    {
        IsFullScreen = _modes.IsFullScreen;
        IsMiniView = _modes.IsMiniView;
    }

    // Instance methods because x:Bind function bindings call them on the page.
    // Segoe Fluent Icons: PlaySolid, PauseBold; Volume1 to Volume3, Mute; FullScreen, BackToWindow.
    public string PlayPauseGlyph(bool isPaused) => isPaused ? "" : "";

    // The play triangle's weight sits left of its box; nudged so it looks centred in the disc.
    public Thickness PlayPauseGlyphMargin(bool isPaused) => isPaused ? new Thickness(3, 0, 0, 0) : new Thickness(0);

    public string PlayPauseName(bool isPaused) => isPaused ? AppText.Get("web.player.controls.play") : AppText.Get("web.player.controls.pause");

    public string PlayPauseToolTip(bool isPaused) => isPaused ? AppText.Get("granary.player.playTooltip") : AppText.Get("granary.player.pauseTooltip");

    public string VolumeGlyph(bool isMuted, double volume) => isMuted || volume <= 0 ? "" : volume switch
    {
        < 34 => "",
        < 67 => "",
        _ => "",
    };

    public string MuteName(bool isMuted) => isMuted ? AppText.Get("web.player.volume.unmute") : AppText.Get("web.player.volume.mute");

    public string MuteToolTip(bool isMuted) => isMuted ? AppText.Get("web.player.volume.unmuteShortcut") : AppText.Get("web.player.volume.muteShortcut");

    public double VolumeOpacity(bool isMuted) => isMuted ? 0.45 : 1;

    public string FullScreenGlyph(bool isFullScreen) => isFullScreen ? "" : "";

    public string FullScreenName(bool isFullScreen) => isFullScreen ? AppText.Get("granary.player.exitFullScreen") : AppText.Get("granary.player.fullScreen");

    public string FullScreenToolTip(bool isFullScreen) =>
        isFullScreen ? AppText.Get("granary.player.exitFullScreenTooltip") : AppText.Get("granary.player.fullScreenTooltip");

    // A utility button that is on is white; off, the rail's 78 %.
    public Brush UtilityForeground(bool isOn) => new SolidColorBrush(isOn ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(0xC7, 0xFF, 0xFF, 0xFF));

    public string OnStatus(bool isOn) => isOn ? AppText.Get("granary.player.on") : "";

    // What the mini view and the bar leave out: the full controls, the pills, the playback info and the Playing Next screen.
    public Visibility ShowsInWindow(bool shows, bool isCompact) => shows && !isCompact ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShowsTopChrome(bool isNextUpScreenShown, bool isCompact) => ShowsInWindow(!isNextUpScreenShown, isCompact);

    // "31:55 / 48:41", or "31:55 / -16:46" with the time left.
    public string TimesText(double position, double duration, bool showsRemaining) =>
        PlayerTime.Format(position, duration) + " / "
        + (showsRemaining ? PlayerTime.Remaining(position, duration) : PlayerTime.Format(duration));

    // The bar's times for screen readers, with its end clock, which a narrow bar leaves out: "4:05 / 48:41, ends at 22:41".
    public string DockTimesName(double position, double duration, bool showsTimeLeft, string endsAtClock) =>
        endsAtClock.Length == 0
            ? TimesText(position, duration, showsTimeLeft)
            : AppText.Get("granary.player.dockTimesName", ("times", TimesText(position, duration, showsTimeLeft)), ("time", endsAtClock));

    // Upper case on screen only: screen readers get the line as written, so "S1 · E1" is not spelled out.
    public string MetaText(string subtitle) => AppText.Upper(subtitle);

    // Named for what it does; screen readers hear the times from the seek bar ("4 minutes of 1 hour 30 minutes").
    public string EndTimeName(bool showsRemaining) =>
        showsRemaining ? AppText.Get("granary.player.showDuration") : AppText.Get("granary.player.showTimeLeft");

    public Visibility ShowsTransport(PlayerPhase phase, bool isNextUpScreenShown, bool isCompact) =>
        ShowsInWindow(phase is PlayerPhase.Playing or PlayerPhase.Ended && !isNextUpScreenShown, isCompact);

    // Preparing at once; later a picture that stands still, once it has (PlayerViewModel.IsWaiting). The bar has no room
    // for the mark: its picture shows the wait, and the loading loop does not run unseen.
    public bool ShowsSpinner(PlayerPhase phase, bool isWaiting, bool isDocked) =>
        !isDocked && (phase == PlayerPhase.Preparing || (phase == PlayerPhase.Playing && isWaiting));

    public Visibility SpinnerVisibility(PlayerPhase phase, bool isWaiting, bool isDocked) =>
        ShowsSpinner(phase, isWaiting, isDocked) ? Visibility.Visible : Visibility.Collapsed;

    // A new stream loads during a plan change; the corner mark names the change itself.
    public string SpinnerName(PlayerPhase phase, bool isChangingPlan) =>
        phase == PlayerPhase.Preparing || isChangingPlan ? AppText.Get("web.player.loadingVideo") : AppText.Get("web.player.bufferingStatus");

    public Visibility Shows(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    // A bar button that needs something, such as chapters, while there is that and Settings lets it show.
    public Visibility ShowsButton(bool has, bool shown) => has && shown ? Visibility.Visible : Visibility.Collapsed;
}
