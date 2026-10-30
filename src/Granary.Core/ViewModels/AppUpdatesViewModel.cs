using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Updates;
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

    /// <summary>
    /// With automatic updates off: a newer release is found, or downloaded and held back, and installs only once the user
    /// chooses Download and install.
    /// </summary>
    Available,

    /// <summary>A release is downloaded and applies when Granary closes or restarts.</summary>
    Ready,

    /// <summary>The last look failed; the next one tries again.</summary>
    Failed,

    /// <summary>The release found could not be verified as signed, so nothing is applied; the next look tries again.</summary>
    NotVerified,

    /// <summary>This copy does not update itself (<see cref="IAppUpdater.Support"/>).</summary>
    Unsupported,
}

/// <summary>
/// Keeps an installed Granary up to date (docs/release.md). With automatic updates on (the default), after the start and
/// every few hours while it runs it looks for a newer release and downloads it in the background, keeping it only once it is
/// verified as a signed release; Settings → About looks at once on Check for updates. Once a release is ready, a notice
/// offers to restart into it; left alone, it is applied when Granary closes. With them off, Granary looks only on Check for
/// updates and only says what it found; nothing is downloaded or applied until the user chooses Download and install. One
/// look or download runs at a time, whoever asked.
/// </summary>
public sealed partial class AppUpdatesViewModel : ObservableObject
{
    /// <summary>How often a running Granary looks again, as Plezy's six hours.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    private const string AutomaticSummary =
        "Granary looks for a new release when it starts and every six hours, downloads it in the background, and installs it when it closes.";

    private static readonly CultureInfo English = CultureInfo.InvariantCulture;

    private readonly IAppUpdater _updater;
    private readonly IUpdatePreferences _preferences;
    private readonly TimeProvider _time;
    private readonly ILogger<AppUpdatesViewModel> _logger;
    private bool _restart;
    private Task? _looking;
    // The release downloaded and verified, kept while it is held back so that confirming it needs no second download.
    private AppRelease? _downloaded;
    // Cancelled when the switch flips, which ends the schedule's wait.
    private CancellationTokenSource _switched = new();
    // The app's run, which ends as the window closes; a look asked for from the page stops with it too.
    private CancellationToken _stopping;

    public AppUpdatesViewModel(IAppUpdater updater, IUpdatePreferences preferences, TimeProvider time, ILogger<AppUpdatesViewModel> logger)
    {
        _updater = updater;
        _preferences = preferences;
        _time = time;
        _logger = logger;
        State = updater.Support == AppUpdateSupport.Supported ? AppUpdateState.Idle : AppUpdateState.Unsupported;
        IsAutomatic = preferences.IsAutomatic;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsBusy), nameof(IsReady), nameof(ReadyVersion), nameof(NoticeText), nameof(ReleaseNotes),
        nameof(HasReleaseNotes), nameof(IsAvailable), nameof(AvailableVersion), nameof(ShowsCheckNow))]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand), nameof(RestartNowCommand), nameof(DownloadAndInstallCommand))]
    public partial AppUpdateState State { get; private set; }

    /// <summary>The release found: being downloaded, available, or ready.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ReadyVersion), nameof(NoticeText), nameof(ReleaseNotes), nameof(HasReleaseNotes),
        nameof(AvailableVersion))]
    public partial AppRelease? Release { get; private set; }

    /// <summary>
    /// Settings → About's Automatic updates, kept in <see cref="IUpdatePreferences"/>. Off, Granary looks only when asked and
    /// holds back an update it has downloaded until the user chooses to install it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(Summary), nameof(RestartText))]
    public partial bool IsAutomatic { get; set; }

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

    /// <summary>Whether this copy updates itself; Automatic updates shows only then.</summary>
    public bool CanUpdate => _updater.Support == AppUpdateSupport.Supported;

    public bool IsBusy => State is AppUpdateState.Checking or AppUpdateState.Downloading;

    public bool IsReady => State == AppUpdateState.Ready;

    /// <summary>With automatic updates off, a newer release waits for Download and install.</summary>
    public bool IsAvailable => State == AppUpdateState.Available;

    /// <summary>Check for updates shows unless Download and install or Restart takes its place.</summary>
    public bool ShowsCheckNow => !IsReady && !IsAvailable;

    /// <summary>The version found while automatic updates are off; only the version, as nothing of it is verified yet.</summary>
    public string? AvailableVersion => IsAvailable ? Release?.Version : null;

    /// <summary>The downloaded release's version, once one is ready to apply.</summary>
    public string? ReadyVersion => IsReady ? Release?.Version : null;

    public string NoticeText => ReadyVersion is { } version ? $"Granary {version} is ready to install" : "";

    /// <summary>What the ready release changes, as it was packed with; empty without.</summary>
    public string ReleaseNotes => IsReady ? Release?.Notes ?? "" : "";

    public bool HasReleaseNotes => ReleaseNotes.Length > 0;

    /// <summary>Settings → About's button for a ready update.</summary>
    public string RestartText => IsAutomatic ? "Restart now" : "Restart to update";

    /// <summary>What Granary does about updates on its own, under the Updates heading.</summary>
    public string Summary => IsAutomatic || !CanUpdate
        ? AutomaticSummary
        : "Granary looks for a new release only when you check, and installs it only once you choose to.";

    /// <summary>One line on where the update stands.</summary>
    public string StatusText => State switch
    {
        AppUpdateState.Unsupported => _updater.Support == AppUpdateSupport.NotInstalled
            ? "This copy wasn't installed with Granary's setup, so it doesn't update itself."
            : "This build doesn't look for updates.",
        AppUpdateState.Checking => "Checking for updates…",
        AppUpdateState.Downloading => $"Downloading Granary {Release?.Version}… {DownloadPercent.ToString(English)}%",
        AppUpdateState.UpToDate => "Granary is up to date.",
        AppUpdateState.Available => $"Granary {Release?.Version} is available.",
        AppUpdateState.Ready => $"Granary {Release?.Version} is ready. It installs when you restart or close Granary.",
        AppUpdateState.Failed => "Couldn't check for updates. Check the connection and try again.",
        AppUpdateState.NotVerified => IsAutomatic
            ? $"The update to Granary {Release?.Version} couldn't be verified, so it wasn't installed. Granary will try again later."
            : $"The update to Granary {Release?.Version} couldn't be verified, so it wasn't installed.",
        _ => IsAutomatic ? "Granary looks for updates when it starts and every six hours." : "Automatic updates are off.",
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
    /// Looks for updates while automatic updates are on, until one is ready or <paramref name="cancellationToken"/> ends; at
    /// once done when this copy cannot update itself. Off, it waits for them to be switched on, and then picks up the
    /// schedule: six hours after the last check, or at once. A failed look is logged and tried again at the next one.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_updater.Support != AppUpdateSupport.Supported)
        {
            return;
        }
        _stopping = cancellationToken;
        var wait = TimeSpan.Zero;
        while (!cancellationToken.IsCancellationRequested && !IsReady)
        {
            // Taken before the switch is read: a flip from here on ends the wait below.
            var switched = _switched.Token;
            if (IsAutomatic && wait <= TimeSpan.Zero)
            {
                await LookAsync(cancellationToken);
                wait = CheckEvery;
                continue;
            }
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, switched);
            try
            {
                await Task.Delay(IsAutomatic ? wait : Timeout.InfiniteTimeSpan, _time, waiting.Token);
                wait = TimeSpan.Zero;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                wait = UntilNextLook();
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

    private bool CanCheckNow() => State is AppUpdateState.Idle or AppUpdateState.UpToDate or AppUpdateState.Failed or AppUpdateState.NotVerified;

    /// <summary>
    /// The user's yes to the release found with automatic updates off: downloads and verifies it as an automatic update
    /// would, or takes the one already downloaded and held back. Ready, it installs when Granary closes or restarts.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsAvailable))]
    private Task DownloadAndInstallAsync() =>
        _looking is { IsCompleted: false } running ? running : _looking = InstallOnceAsync(_stopping);

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

    partial void OnIsAutomaticChanged(bool value)
    {
        if (value != _preferences.IsAutomatic)
        {
            // Never fails: a save that does not work is logged, and the choice holds for this run.
            _ = _preferences.SetAutomaticAsync(value);
        }
        if (!value && IsReady)
        {
            // Off means nothing installs without asking: a downloaded update waits for Download and install.
            _logger.LogInformation("Automatic updates are off: Granary {Version} waits to be installed", Release?.Version);
            IsNoticeShown = false;
            State = AppUpdateState.Available;
        }
        else if (value && IsAvailable && _downloaded is { } held)
        {
            Release = held;
            State = AppUpdateState.Ready;
        }
        // Last, after the state is settled: the schedule's wait ends and it looks at the switch again.
        var flipped = _switched;
        _switched = new CancellationTokenSource();
        flipped.Cancel();
    }

    // Switched on: the next look is six hours after the last check, or at once; at once too for a release found while off.
    private TimeSpan UntilNextLook()
    {
        if (IsAvailable || LastCheckedAt is not { } last)
        {
            return TimeSpan.Zero;
        }
        var left = last + CheckEvery - _time.GetUtcNow();
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    // One look at a time: the timer's and the button's share it, and Download and install's.
    private Task LookAsync(CancellationToken cancellationToken) =>
        _looking is { IsCompleted: false } running ? running : _looking = LookOnceAsync(cancellationToken);

    private async Task LookOnceAsync(CancellationToken cancellationToken)
    {
        if (State is AppUpdateState.Ready or AppUpdateState.Unsupported || (IsAvailable && _downloaded is not null))
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
            if (!IsAutomatic)
            {
                // Only the version: the feed's notes are not signed, and nothing is downloaded until the user says so.
                State = AppUpdateState.Available;
                return;
            }
            await DownloadAsync(confirmed: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Granary is closing.
        }
        catch (Exception ex)
        {
            // Offline, rate-limited or a broken release: the app goes on as it is and looks again later. The kind of
            // failure only: Velopack's and HttpClient's messages can carry addresses.
            _logger.LogWarning("Could not look for a Granary update: {Problem}", UpdateFailures.KindOf(ex));
            Release = null;
            State = AppUpdateState.Failed;
        }
    }

    private async Task InstallOnceAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return;
        }
        if (_downloaded is { } held)
        {
            // Verified when it was downloaded, and hashed once more as Granary closes.
            Release = held;
            State = AppUpdateState.Ready;
            return;
        }
        try
        {
            await DownloadAsync(confirmed: true, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Granary is closing.
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not download the Granary update: {Problem}", UpdateFailures.KindOf(ex));
            Release = null;
            State = AppUpdateState.Failed;
        }
    }

    // Downloads the release found and keeps it only once it is verified. Unless the user asked for it, it is held back
    // when automatic updates were switched off meanwhile.
    private async Task DownloadAsync(bool confirmed, CancellationToken cancellationToken)
    {
        _downloaded = null;
        DownloadPercent = 0;
        State = AppUpdateState.Downloading;
        // Made here, on the caller's thread, so the updater's reports come back to it.
        var verified = await _updater.DownloadAsync(new Progress<int>(percent => DownloadPercent = Math.Clamp(percent, 0, 100)),
            cancellationToken);
        if (verified is null)
        {
            // The updater logged why. Nothing is applied; the next look tries again.
            State = AppUpdateState.NotVerified;
            return;
        }
        // From here on, the notes shown are the ones the signed package carries.
        Release = verified;
        _downloaded = verified;
        _logger.LogInformation("Granary {Version} is downloaded and ready to install", verified.Version);
        if (!confirmed && !IsAutomatic)
        {
            State = AppUpdateState.Available;
            return;
        }
        State = AppUpdateState.Ready;
        IsNoticeShown = true;
    }
}
