using System.Runtime;
using Granary.App.Services;
using Granary.App.Themes;
using Granary.Core;
using Granary.Core.Abstractions;
using Granary.Core.Diagnostics;
using Granary.Core.Downloads;
using Granary.Core.Overlays;
using Granary.Core.Playback;
using Granary.Core.Playback.Themes;
using Granary.Core.Realtime;
using Granary.Core.Remote;
using Granary.Core.Servers;
using Granary.Core.Settings;
using Granary.Core.Theme;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
using Granary.Core.ViewModels.Downloads;
using Granary.Player;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Granary.App;

public partial class App : Application
{
    // The events connection's close at exit: a server that does not answer is not waited for longer.
    private static readonly TimeSpan RealtimeStopBudget = TimeSpan.FromSeconds(2.5);

    private MainWindow? _window;
    private ILogger<App>? _logger;

    public App()
    {
        InitializeComponent();
        // A blocking full collection mid-scroll cost frames (up to 60 ms); while memory allows, collections run beside the app.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        UnhandledException += (_, e) => ReportUnhandled(e.Exception);
    }

    /// <summary>The composition root. Pages resolve their view models from here.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Launch();
        }
        catch (Exception ex)
        {
            // The logger may not exist yet; leave a trace for the user and for support.
            Directory.CreateDirectory(FileLoggerProvider.DefaultDirectory);
            File.AppendAllText(Path.Combine(FileLoggerProvider.DefaultDirectory, "startup-crash.log"),
                $"{DateTime.Now:O} {LogRedactor.Redact(ex.ToString())}{Environment.NewLine}");
            throw;
        }
    }

    // Never throws: WinUI reports a failing handler as one more unhandled exception from within, again and again, until the
    // stack overflows. A log that could not be written did that, and hid what had gone wrong.
    private void ReportUnhandled(Exception exception)
    {
        try
        {
            _logger?.LogCritical(exception, "Unhandled exception");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"An unhandled {exception.GetType().Name} could not be logged: {ex.GetType().Name}");
        }
    }

    private void Launch()
    {
        Services = ConfigureServices(DispatcherQueue.GetForCurrentThread());
        _logger = Services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("Granary {Version} starting", typeof(App).Assembly.GetName().Version);
        _window = Services.GetRequiredService<MainWindow>();
        // Windows' notifications for downloads that finish or fail while Granary is not in front, registered at every start
        // before anything reads how Granary was started: a click on one while Granary was closed started it, and its Play or
        // Open Downloads runs once the shell is up (docs/architecture.md, "Downloads in the shell").
        var notices = Services.GetRequiredService<DownloadNotices>();
        _window.Closed += (_, _) => notices.Dispose();
        var startedByClick = Services.GetRequiredService<WindowsAppNotifications>()
            .Register(_window, click => notices.Act(click.Action, click.DownloadId));
        // Held until the shell has started, as any click that comes while Granary restores its sign-in.
        if (startedByClick is { } click)
        {
            notices.Act(click.Action, click.DownloadId);
        }
        var started = new TaskCompletionSource();
        _window.ShowStartup(started.Task);
        // Activated, and maximized at once if it was closed so.
        Services.GetRequiredService<WindowModeService>().Show();
#if DEBUG
        // Developer shortcut: --remote shows a TV remote on screen beside the window (Debugging/RemoteWindow.cs).
        if (Environment.GetCommandLineArgs().Contains("--remote"))
        {
            new Debugging.RemoteWindow(_window).Show();
        }
#endif
        // A server on this network is reached directly, once its key is verified (docs/api-contract.md, 9.7).
        Services.GetRequiredService<LocalRouteMonitor>().Start();
        // Downloads carry on where they stopped, for the profile in use (docs/architecture.md, "The queue"). Closing stops
        // them and waits a moment for their files to be let go; a part file is resumed at the next start.
        var downloads = Services.GetRequiredService<DownloadQueue>();
        downloads.Start();
        _window.Closed += (_, _) => downloads.StopAsync().Wait(DownloadQueue.StopBudget);
        // The sidebar and the taskbar button follow them for the whole run (docs/architecture.md, "Downloads in the shell").
        var activity = Services.GetRequiredService<DownloadActivity>();
        activity.Start();
        _window.Closed += (_, _) => activity.Dispose();
        Services.GetRequiredService<WindowsTaskbarProgress>().Start();
        // What was watched from this PC goes to the server once it can be reached, and its progress comes back
        // (docs/architecture.md, "Offline progress"). The last position of a title that plays as Granary closes is kept
        // with its stop, which the window waits for.
        var progress = Services.GetRequiredService<OfflineProgressSync>();
        progress.Start();
        _window.Closed += (_, _) => progress.Dispose();
        // Monitored series bring their new episodes: at the start, with each profile, every four hours and after a change
        // (docs/architecture.md, "Series monitoring").
        var monitors = Services.GetRequiredService<SeriesMonitors>();
        monitors.Start();
        _window.Closed += (_, _) => monitors.Dispose();
        // The server's accent recolours Granary as it arrives, and with every other server.
        var accent = Services.GetRequiredService<ServerAccent>();
        accent.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServerAccent.Palette))
            {
                AccentPainter.Apply(accent.Palette);
            }
        };
        _ = accent.StartAsync();
        // The profile's card badges, read with every sign-in and profile switch.
        _ = Services.GetRequiredService<CardOverlaySettings>().StartAsync();
        // The profile's poster size and caption, likewise.
        _ = Services.GetRequiredService<CardPresentationSettings>().StartAsync();
        // The profile's spoiler switches, likewise.
        _ = Services.GetRequiredService<EpisodeSpoilerSettings>().StartAsync();
        // And the rest of its Title Pages settings.
        _ = Services.GetRequiredService<TitlePageSettings>().StartAsync();
        // Home and the sidebar's count follow what happens on other devices (docs/api-contract.md, 12). Closing tells the
        // server the connection goes, but waits for that only a moment: the stop never needs this thread.
        var realtime = Services.GetRequiredService<RealtimeEvents>();
        // A new notification syncs the monitored series, as a push does in Silo's Apple app: it may be a new episode's.
        realtime.NotificationArrived += (_, _) => monitors.SyncSoon();
        realtime.Start();
        _window.Closed += (_, _) => realtime.StopAsync().Wait(RealtimeStopBudget);
        // The phone remote follows its switch, the session and the PC's address from now on (Settings → Playback → This PC).
        // Closing lets every phone go, takes the advertisement back and closes the port, within its budget.
        // A phone starts no title on a locked PC: the window hears of the lock until it closes.
        var pcLock = Services.GetRequiredService<WindowsPcLock>();
        pcLock.Follow(WinRT.Interop.WindowNative.GetWindowHandle(_window));
        _window.Closed += (_, _) => pcLock.Dispose();
        var remote = Services.GetRequiredService<RemoteReceiver>();
        remote.Start();
        _window.Closed += (_, _) => remote.StopAsync().Wait(RemoteReceiver.StopBudget);
        // The Discord status goes with the app.
        _window.Closed += (_, _) => Services.GetRequiredService<DiscordStatus>().Dispose();

        // Developer shortcuts: --play <file or URL> plays it without the server; --play-item <content id> plays a
        // title of the signed-in server.
        StartShell(_window.Shell, DeveloperPlayRequest() is null ? null : AppRoute.Player, DeveloperPlayRequest(), started,
            notices.ShellStarted);
    }

    private static PlayRequest? DeveloperPlayRequest()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        int file = Array.IndexOf(arguments, "--play");
        if (file >= 0 && file + 1 < arguments.Length)
        {
            return PlayRequest.Direct(arguments[file + 1]);
        }
        int item = Array.IndexOf(arguments, "--play-item");
        return item >= 0 && item + 1 < arguments.Length ? PlayRequest.Resume(arguments[item + 1]) : null;
    }

    // Tells the start screen when the shell has started (or failed to), so it can make way; then does what a notification's
    // click held until now asked for (the one that started Granary, or one while it started), over the page the shell routed to.
    private async void StartShell(ShellViewModel shell, AppRoute? initialRoute, object? parameter, TaskCompletionSource started, Action? then)
    {
        try
        {
            await shell.StartAsync(initialRoute, parameter);
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "The shell failed to start");
            throw;
        }
        finally
        {
            started.TrySetResult();
        }
        try
        {
            then?.Invoke();
        }
        catch (Exception ex)
        {
            // async void: nothing may escape; the app is up all the same.
            _logger?.LogWarning("A notification's click could not be followed at the start ({Failure})", ex.GetType().Name);
        }
    }

    private static ServiceProvider ConfigureServices(DispatcherQueue dispatcherQueue)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder
            .SetMinimumLevel(LogLevel.Debug)
            .AddProvider(new FileLoggerProvider(FileLoggerProvider.DefaultDirectory)));
        services.AddSingleton<IUiDispatcher>(new UiDispatcher(dispatcherQueue));
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());
        services.AddSingleton<INetworkLink, WindowsNetworkLink>();
        services.AddTransient<ILanServiceBrowser, DnssdLanServiceBrowser>();
        services.AddSingleton<IAppUpdater, VelopackUpdater>();
        services.AddSingleton<IAppInfo, BuildInfo>();
        services.AddSingleton<IConfirmation, DialogConfirmation>();
        services.AddSingleton<ISystemMediaControls, WindowsMediaControls>();
        // Discord's status while a title plays (Settings → Playback → This PC); it connects only once there is one to show.
        services.AddSingleton<DiscordStatus>();
        services.AddSingleton<IRichPresence>(sp => sp.GetRequiredService<DiscordStatus>());
        services.AddSingleton<IFileLauncher, ShellFileLauncher>();
        // The main window's full screen and mini view, shared by the app, the player and a trailer; and where the window
        // opens, where it was closed (window.json).
        services.AddSingleton(sp => new WindowPlaceFile(WindowPlaceFile.DefaultPath, sp.GetRequiredService<ILogger<WindowPlaceFile>>()));
        services.AddSingleton<WindowModeService>();
        // Previous episode, Play/Pause and Next episode in the main window's taskbar preview while a title plays, and the
        // downloads' progress on its taskbar button while none does.
        services.AddSingleton<WindowsTaskbarToolbar>();
        services.AddSingleton<ITaskbarToolbar>(sp => sp.GetRequiredService<WindowsTaskbarToolbar>());
        services.AddSingleton<WindowsTaskbarProgress>();
        // Keeps the display on while a title plays.
        services.AddSingleton<IKeepAwake, WindowsKeepAwake>();
        // Windows' notifications for downloads that finish or fail while Granary is not in front; the app registers them.
        services.AddSingleton<WindowsAppNotifications>();
        services.AddSingleton<IAppNotifications>(sp => sp.GetRequiredService<WindowsAppNotifications>());
        // Title pages' theme music, through Windows' own media player beside mpv.
        services.AddSingleton<IThemeAudioOutput, WindowsThemeAudio>();
        // The free space where downloads go, as Windows counts it, network shares included; Core's own count knows only drives.
        services.AddSingleton<IDiskSpace, WindowsDiskSpace>();
        // Settings → Downloads' Change: Windows' folder picker over the main window.
        services.AddSingleton<IFolderPicker, WindowsFolderPicker>();
        // A title's Download: the web's download dialog over the main window. Core's own shows nothing.
        services.AddSingleton<IDownloadOptionsDialog, DownloadOptionsDialog>();
        // The phone remote's ports: a TcpListener on the PC's LAN address, Windows' own DNS-SD registration, and that
        // address from Windows' adapters. Before Core, whose do-nothing stand-ins they replace.
        services.AddSingleton<IRemoteListener, TcpRemoteListener>();
        services.AddSingleton<IServiceAdvertiser, DnssdServiceAdvertiser>();
        services.AddSingleton<ILanAddressSource, LanAddressSource>();
        // Whether Windows is locked, from the session's notices for the main window: a phone starts no title then.
        services.AddSingleton<WindowsPcLock>();
        services.AddSingleton<IPcLock>(sp => sp.GetRequiredService<WindowsPcLock>());
        services.AddGranaryCore();
        // mpv reads the viewer's own mpv.conf from its folder when it starts (Settings → Playback → This PC).
        services.AddGranaryPlayer(new MpvEngineOptions { ConfigDirectory = MpvConfigFolder.DefaultDirectory });
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
