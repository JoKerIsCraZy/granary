using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>
/// Keeps an installed Granary up to date (docs/release.md): after the start, and every few hours while it runs, it looks
/// for a newer release and downloads it in the background. Once one is ready, a notice offers to restart into it; left
/// alone, it is applied when Granary closes.
/// </summary>
public sealed partial class AppUpdatesViewModel(IAppUpdater updater, TimeProvider time, ILogger<AppUpdatesViewModel> logger)
    : ObservableObject
{
    /// <summary>How often a running Granary looks again, as Plezy's six hours.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    private bool _restart;

    /// <summary>The downloaded release's version, once one is ready to apply.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoticeText))]
    public partial string? ReadyVersion { get; private set; }

    /// <summary>Whether the notice of a ready update shows; dismissing it leaves the update to the next close.</summary>
    [ObservableProperty]
    public partial bool IsNoticeShown { get; private set; }

    public string NoticeText => ReadyVersion is { } version ? $"Granary {version} is ready to install" : "";

    /// <summary>Asks the window to close, so a restart into the update passes through the usual close (playback stops first).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Looks for updates until one is downloaded or <paramref name="cancellationToken"/> ends; at once done when this copy
    /// cannot update itself. A failed look is logged and tried again at the next one.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!updater.CanUpdate)
        {
            return;
        }
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (await updater.CheckAndDownloadAsync(cancellationToken) is { } version)
                {
                    logger.LogInformation("Granary {Version} is downloaded and ready to install", version);
                    ReadyVersion = version;
                    IsNoticeShown = true;
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Offline, rate-limited or a broken release: the app goes on as it is and looks again later.
                logger.LogWarning(ex, "Could not look for a Granary update");
            }
            try
            {
                await Task.Delay(CheckEvery, time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Restarts into the downloaded release: the window closes as usual, the update is applied, Granary starts again.</summary>
    [RelayCommand]
    private void RestartNow()
    {
        if (ReadyVersion is null)
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
        if (ReadyVersion is not null)
        {
            updater.ApplyWhenExited(_restart);
        }
    }
}
