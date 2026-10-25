using Granary.App.Logging;
using Granary.App.Services;
using Granary.Core;
using Granary.Core.Abstractions;
using Granary.Core.Diagnostics;
using Granary.Core.Playback;
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
        _window.Activate();

        // Developer shortcut: Granary.exe --play <file or URL> plays it without the server.
        string[] arguments = Environment.GetCommandLineArgs();
        int play = Array.IndexOf(arguments, "--play");
        bool playFile = play >= 0 && play + 1 < arguments.Length;
        StartShell(_window.Shell, playFile ? AppRoute.Player : null, playFile ? PlayRequest.Direct(arguments[play + 1]) : null);
    }

    private async void StartShell(ShellViewModel shell, AppRoute? initialRoute, object? parameter)
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
        services.AddGranaryCore();
        services.AddGranaryPlayer();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
