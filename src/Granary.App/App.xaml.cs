using System.Runtime;
using Granary.App.Logging;
using Granary.App.Services;
using Granary.Core;
using Granary.Core.Abstractions;
using Granary.Core.Diagnostics;
using Granary.Core.Playback;
using Granary.Core.Servers;
using Granary.Core.ViewModels;
using Granary.Player;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Granary.App;

public partial class App : Application
{
    private MainWindow? _window;
    private ILogger<App>? _logger;

    public App()
    {
        InitializeComponent();
        // A blocking full collection mid-scroll cost frames (up to 60 ms); while memory allows, collections run beside the app.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        UnhandledException += (_, e) => _logger?.LogCritical(e.Exception, "Unhandled exception");
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

    private void Launch()
    {
        Services = ConfigureServices(DispatcherQueue.GetForCurrentThread());
        _logger = Services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("Granary {Version} starting", typeof(App).Assembly.GetName().Version);
        _window = Services.GetRequiredService<MainWindow>();
        var started = new TaskCompletionSource();
        _window.ShowStartup(started.Task);
        _window.Activate();
        // A server on this network is reached directly, once its key is verified (docs/api-contract.md, 9.7).
        Services.GetRequiredService<LocalRouteMonitor>().Start();

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
        services.AddSingleton<IAppUpdater, VelopackUpdater>();
        services.AddSingleton<IAppInfo, BuildInfo>();
        services.AddGranaryCore();
        services.AddGranaryPlayer();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
