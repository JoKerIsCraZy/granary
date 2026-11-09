using System.Runtime;
using Granary.App.Services;
using Granary.App.Themes;
using Granary.Core;
using Granary.Core.Abstractions;
using Granary.Core.Diagnostics;
using Granary.Core.Overlays;
using Granary.Core.Playback;
using Granary.Core.Playback.Themes;
using Granary.Core.Realtime;
using Granary.Core.Servers;
using Granary.Core.Settings;
using Granary.Core.Theme;
using Granary.Core.ViewModels;
using Granary.Core.ViewModels.Cards;
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
        var started = new TaskCompletionSource();
        _window.ShowStartup(started.Task);
        _window.Activate();
#if DEBUG
        // Developer shortcut: --remote shows a TV remote on screen beside the window (Debugging/RemoteWindow.cs).
        if (Environment.GetCommandLineArgs().Contains("--remote"))
        {
            new Debugging.RemoteWindow(_window).Show();
        }
#endif
        // A server on this network is reached directly, once its key is verified (docs/api-contract.md, 9.7).
        Services.GetRequiredService<LocalRouteMonitor>().Start();
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
        realtime.Start();
        _window.Closed += (_, _) => realtime.StopAsync().Wait(RealtimeStopBudget);
        // The Discord status goes with the app.
        _window.Closed += (_, _) => Services.GetRequiredService<DiscordStatus>().Dispose();

        // Developer shortcuts: --play <file or URL> plays it without the server; --play-item <content id> plays a
        // title of the signed-in server.
        StartShell(_window.Shell, DeveloperPlayRequest() is null ? null : AppRoute.Player, DeveloperPlayRequest(), started);
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

    // Tells the start screen when the shell has started (or failed to), so it can make way.
    private async void StartShell(ShellViewModel shell, AppRoute? initialRoute, object? parameter, TaskCompletionSource started)
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
        // Previous episode, Play/Pause and Next episode in the main window's taskbar preview while a title plays.
        services.AddSingleton<ITaskbarToolbar, WindowsTaskbarToolbar>();
        // Keeps the display on while a title plays.
        services.AddSingleton<IKeepAwake, WindowsKeepAwake>();
        // Title pages' theme music, through Windows' own media player beside mpv.
        services.AddSingleton<IThemeAudioOutput, WindowsThemeAudio>();
        services.AddGranaryCore();
        // mpv reads the viewer's own mpv.conf from its folder when it starts (Settings → Playback → This PC).
        services.AddGranaryPlayer(new MpvEngineOptions { ConfigDirectory = MpvConfigFolder.DefaultDirectory });
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
