using Granary.App.Interop;
using Granary.Core.Abstractions;
using Granary.Core.Downloads;
using Granary.Core.ViewModels.Downloads;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Granary.App.Services;

/// <summary>A notification's click: what it asks for (<see cref="DownloadNotices.Act"/>) and the download it names.</summary>
internal readonly record struct NotificationClick(string? Action, string? DownloadId);

/// <summary>
/// Windows' notifications for Granary, through the Windows App SDK (<c>AppNotificationManager</c>), for an unpackaged app
/// (docs/architecture.md, "Downloads in the shell"). <see cref="Register"/> runs at every start, before the activation is read:
/// it takes the click handler first, so a click while Granary runs comes to this process, then registers Granary for Windows'
/// notifications, which for an unpackaged app writes its notification id, display name, icon and COM activator under the
/// user's registry (HKCU) and serves that activator in this process. A click while Granary is closed starts it with
/// <c>----AppNotificationActivated:</c>, and <see cref="Register"/> answers that click. A notification has Play when something
/// finished and Open Downloads; its body opens Downloads too. Windows' side cannot be unit-tested: <c>DownloadNotices</c> holds
/// what is said and what a click does, and is (<c>DownloadNoticesTests</c>).
/// </summary>
internal sealed class WindowsAppNotifications(ILogger<WindowsAppNotifications> logger) : IAppNotifications
{
    private const string ActionKey = "action";
    private const string DownloadKey = "download";

    private MainWindow? _window;
    private Microsoft.UI.Dispatching.DispatcherQueue? _ui;
    private Action<NotificationClick>? _clicked;
    private bool _registered;
    private bool _failureLogged;

    public bool IsAppInFront => _window is not { } window || ForegroundWindow.Is(WinRT.Interop.WindowNative.GetWindowHandle(window));

    /// <summary>
    /// Registers Granary for Windows' notifications, with <paramref name="clicked"/> for their clicks, run on the UI thread with
    /// <paramref name="window"/> brought to the front; answers the click that started this run, if a notification's did. On a PC
    /// or for a user without them (Granary run as an administrator, a registration that fails) it answers none, and every
    /// message shows in the window.
    /// </summary>
    public NotificationClick? Register(MainWindow window, Action<NotificationClick> clicked)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        // Taken here, on the UI thread: a click comes on a thread of COM's.
        _ui = window.DispatcherQueue;
        _clicked = clicked;
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                logger.LogInformation("Windows' notifications are not available to Granary; download messages show in its window");
                return null;
            }
            var manager = AppNotificationManager.Default;
            // The handler before Register: otherwise a click would start another Granary to handle it.
            manager.NotificationInvoked += OnInvoked;
            manager.Register();
            _registered = true;
            window.Closed += (_, _) => Unregister();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Granary could not register for Windows' notifications ({Failure}); download messages show in its window",
                ex.GetType().Name);
            return null;
        }
        try
        {
            // Read only after Register, which a start from a notification needs to answer with its click.
            var activated = AppInstance.GetCurrent().GetActivatedEventArgs();
            return activated.Kind == ExtendedActivationKind.AppNotification && activated.Data is AppNotificationActivatedEventArgs args
                ? ClickOf(args)
                : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning("How Granary was started could not be read ({Failure}); a notification's click that started it is not followed",
                ex.GetType().Name);
            return null;
        }
    }

    public bool TryShow(DownloadAnnouncement announcement)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        if (!_registered)
        {
            return false;
        }
        try
        {
            var manager = AppNotificationManager.Default;
            // Turned off for Granary, or for all apps, in Windows' settings: the window says it instead.
            if (manager.Setting != AppNotificationSetting.Enabled)
            {
                return false;
            }
            var builder = new AppNotificationBuilder()
                .AddArgument(ActionKey, DownloadNotices.OpenAction)
                .AddText(announcement.Text);
            if (announcement.PlayDownloadId is { } downloadId)
            {
                builder.AddButton(new AppNotificationButton(DownloadNotices.PlayLabel)
                    .AddArgument(ActionKey, DownloadNotices.PlayAction)
                    .AddArgument(DownloadKey, downloadId));
            }
            builder.AddButton(new AppNotificationButton(DownloadNotices.OpenDownloadsLabel).AddArgument(ActionKey, DownloadNotices.OpenAction));
            var notification = builder.BuildNotification();
            manager.Show(notification);
            // Windows gives a notification it took an id.
            return notification.Id != 0;
        }
        catch (Exception ex)
        {
            if (!_failureLogged)
            {
                _failureLogged = true;
                logger.LogWarning("A download's message could not be shown as a Windows notification ({Failure})", ex.GetType().Name);
            }
            return false;
        }
    }

    /// <summary>
    /// Takes Granary's notification registration off this PC for the user (Velopack's uninstall, Program.Main, before WinUI or
    /// anything else of the app starts): its notification id, display name, icon and COM activator under HKCU. Never throws: an
    /// uninstall goes on whatever this does.
    /// </summary>
    public static void RemoveRegistration()
    {
        try
        {
            // Program.Main does this only after Velopack's hooks, which end the process.
            WinRT.ComWrappersSupport.InitializeComWrappers();
            if (AppNotificationManager.IsSupported())
            {
                AppNotificationManager.Default.UnregisterAll();
            }
        }
        catch (Exception)
        {
            // Nothing to log to: the app's logs are not set up while Velopack uninstalls it.
        }
    }

    // On a thread of COM's: the click runs on the UI thread, with the window in front.
    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            var click = ClickOf(args);
            _ui?.TryEnqueue(() => Run(click));
        }
        catch (Exception ex)
        {
            logger.LogWarning("A notification's click could not be handled ({Failure})", ex.GetType().Name);
        }
    }

    private void Run(NotificationClick click)
    {
        try
        {
            if (_window is { } window)
            {
                if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
                {
                    presenter.Restore();
                }
                window.Activate();
            }
            _clicked?.Invoke(click);
        }
        catch (Exception ex)
        {
            logger.LogWarning("A notification's click could not be handled ({Failure})", ex.GetType().Name);
        }
    }

    private static NotificationClick ClickOf(AppNotificationActivatedEventArgs args) =>
        new(args.Arguments.TryGetValue(ActionKey, out string? action) ? action : null,
            args.Arguments.TryGetValue(DownloadKey, out string? downloadId) ? downloadId : null);

    // The process goes: its activator goes with it, so a later click starts Granary again.
    private void Unregister()
    {
        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex)
        {
            logger.LogDebug("Granary's notification activator could not be let go ({Failure})", ex.GetType().Name);
        }
    }
}
