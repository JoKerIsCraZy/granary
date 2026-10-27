using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>Where Granary's own update stands.</summary>
public enum AppUpdateState
{
    /// <summary>Not looked yet.</summary>
    Idle,
    Checking,
    Downloading,
    UpToDate,

    /// <summary>A release is downloaded and applies when Granary closes or restarts.</summary>
    Ready,

    /// <summary>The last look failed; the next one tries again.</summary>
    Failed,

    /// <summary>This copy does not update itself (<see cref="IAppUpdater.Support"/>).</summary>
    Unsupported,
}

/// <summary>
/// Keeps an installed Granary up to date (docs/release.md): after the start, and every few hours while it runs, it looks
/// for a newer release and downloads it in the background; Settings → About looks at once on Check for updates. One look
/// runs at a time, whoever asked. Once a release is ready, a notice offers to restart into it; left alone, it is applied
/// when Granary closes.
/// </summary>
public sealed partial class AppUpdatesViewModel : ObservableObject
{
    /// <summary>How often a running Granary looks again, as Plezy's six hours.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    private static readonly CultureInfo English = CultureInfo.InvariantCulture;

    private readonly IAppUpdater _updater;
    private readonly TimeProvider _time;
    private readonly ILogger<AppUpdatesViewModel> _logger;
    private bool _restart;
    private Task? _looking;
    // The app's run, which ends as the window closes; a look asked for from the page stops with it too.
    private CancellationToken _stopping;

    public AppUpdatesViewModel(IAppUpdater updater, TimeProvider time, ILogger<AppUpdatesViewModel> logger)
    {
        _updater = updater;
        _time = time;
        _logger = logger;
        State = updater.Support == AppUpdateSupport.Supported ? AppUpdateState.Idle : AppUpdateState.Unsupported;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsBusy), nameof(IsReady), nameof(ReadyVersion), nameof(NoticeText), nameof(ReleaseNotes),
        nameof(HasReleaseNotes))]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand), nameof(RestartNowCommand))]
    public partial AppUpdateState State { get; private set; }

    /// <summary>The release found: being downloaded, or ready.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ReadyVersion), nameof(NoticeText), nameof(ReleaseNotes), nameof(HasReleaseNotes))]
    public partial AppRelease? Release { get; private set; }

    /// <summary>How much of the release is downloaded, in percent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial int DownloadPercent { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastCheckedText))]
    public partial DateTimeOffset? LastCheckedAt { get; private set; }

    /// <summary>Whether the notice of a ready update shows; dismissing it leaves the update to the next close.</summary>
    [ObservableProperty]
    public partial bool IsNoticeShown { get; private set; }

    public bool IsBusy => State is AppUpdateState.Checking or AppUpdateState.Downloading;

    public bool IsReady => State == AppUpdateState.Ready;

    /// <summary>The downloaded release's version, once one is ready to apply.</summary>
    public string? ReadyVersion => IsReady ? Release?.Version : null;

    public string NoticeText => ReadyVersion is { } version ? $"Granary {version} is ready to install" : "";

    /// <summary>What the ready release changes, as it was packed with; empty without.</summary>
    public string ReleaseNotes => IsReady ? Release?.Notes ?? "" : "";

    public bool HasReleaseNotes => ReleaseNotes.Length > 0;

    /// <summary>One line on where the update stands.</summary>
    public string StatusText => State switch
    {
        AppUpdateState.Unsupported => _updater.Support == AppUpdateSupport.NotInstalled
            ? "This copy wasn't installed with Granary's setup, so it doesn't update itself."
            : "This build doesn't look for updates.",
        AppUpdateState.Checking => "Checking for updates…",
        AppUpdateState.Downloading => $"Downloading Granary {Release?.Version}… {DownloadPercent.ToString(English)}%",
        AppUpdateState.UpToDate => "Granary is up to date.",
        AppUpdateState.Ready => $"Granary {Release?.Version} is ready. It installs when you restart or close Granary.",
        AppUpdateState.Failed => "Couldn't check for updates. Check the connection and try again.",
        _ => "Granary looks for updates when it starts and every six hours.",
    };

    /// <summary>"Last checked today at 14:05", or "on Oct 26 at 09:12"; empty before the first look.</summary>
    public string LastCheckedText
    {
        get
        {
            if (LastCheckedAt is not { } at)
            {
                return "";
            }
            var local = TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone);
            var today = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone).Date;
            string day = local.Date == today ? "today" : "on " + local.ToString("MMM d", English);
            return $"Last checked {day} at {local.ToString("HH:mm", English)}";
        }
    }

    /// <summary>Asks the window to close, so a restart into the update passes through the usual close (playback stops first).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Looks for updates until one is downloaded or <paramref name="cancellationToken"/> ends; at once done when this copy
    /// cannot update itself. A failed look is logged and tried again at the next one.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_updater.Support != AppUpdateSupport.Supported)
        {
            return;
        }
        _stopping = cancellationToken;
        while (!cancellationToken.IsCancellationRequested)
        {
            await LookAsync(cancellationToken);
            if (IsReady)
            {
                return;
            }
            try
            {
                await Task.Delay(CheckEvery, _time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Settings → About's Check for updates: a look now, or the one already running.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private Task CheckNowAsync() => LookAsync(_stopping);

    private bool CanCheckNow() => State is AppUpdateState.Idle or AppUpdateState.UpToDate or AppUpdateState.Failed;

    /// <summary>Restarts into the downloaded release: the window closes as usual, the update is applied, Granary starts again.</summary>
    [RelayCommand(CanExecute = nameof(IsReady))]
    private void RestartNow()
    {
        if (!IsReady)
        {
            return;
        }
        _restart = true;
        IsNoticeShown = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Hides the notice; the update is applied when Granary closes.</summary>
    [RelayCommand]
    private void Later() => IsNoticeShown = false;

    /// <summary>For the window as it closes: hands a downloaded release to the updater, to apply once the app has exited.</summary>
    public void OnExiting()
    {
        if (IsReady)
        {
            _updater.ApplyWhenExited(_restart);
        }
    }

    // One look at a time: the timer's and the button's share it.
    private Task LookAsync(CancellationToken cancellationToken) =>
        _looking is { IsCompleted: false } running ? running : _looking = LookOnceAsync(cancellationToken);

    private async Task LookOnceAsync(CancellationToken cancellationToken)
    {
        if (State is AppUpdateState.Ready or AppUpdateState.Unsupported)
        {
            return;
        }
        State = AppUpdateState.Checking;
        try
        {
            var release = await _updater.CheckAsync(cancellationToken);
            LastCheckedAt = _time.GetUtcNow();
            if (release is null)
            {
                State = AppUpdateState.UpToDate;
                return;
            }
            Release = release;
            DownloadPercent = 0;
            State = AppUpdateState.Downloading;
            // Made here, on the caller's thread, so the updater's reports come back to it.
            await _updater.DownloadAsync(new Progress<int>(percent => DownloadPercent = Math.Clamp(percent, 0, 100)), cancellationToken);
            _logger.LogInformation("Granary {Version} is downloaded and ready to install", release.Version);
            State = AppUpdateState.Ready;
            IsNoticeShown = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Granary is closing.
        }
        catch (Exception ex)
        {
            // Offline, rate-limited or a broken release: the app goes on as it is and looks again later.
            _logger.LogWarning(ex, "Could not look for a Granary update");
            Release = null;
            State = AppUpdateState.Failed;
        }
    }
}
