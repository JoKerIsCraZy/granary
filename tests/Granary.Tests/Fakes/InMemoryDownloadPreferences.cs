using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;

namespace Granary.Tests.Fakes;

/// <summary>This PC's download settings without a file; each change is raised at once, as the real one does.</summary>
internal sealed class InMemoryDownloadPreferences(string folder) : IDownloadPreferences
{
    public string Folder { get; private set; } = folder;

    public bool DownloadsOnMetered { get; private set; }

    public string DefaultQuality { get; private set; } = DownloadQualities.Original;

    public bool KeepWatched { get; private set; }

    public bool NotifyWhenFinished { get; private set; } = true;

    public int SimultaneousDownloads { get; private set; } = DownloadPreferenceLimits.DefaultSimultaneous;

    public int StorageLimitGb { get; private set; }

    public bool DefaultDeleteWatched { get; private set; }

    public int DefaultMonitorLimitGb { get; private set; }

    public Task SetDefaultDeleteWatchedAsync(bool delete)
    {
        DefaultDeleteWatched = delete;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SetDefaultMonitorLimitGbAsync(int gigabytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gigabytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(gigabytes, DownloadPreferenceLimits.MaxMonitorLimitGb);
        DefaultMonitorLimitGb = gigabytes;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SetStorageLimitGbAsync(int gigabytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gigabytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(gigabytes, DownloadPreferenceLimits.MaxStorageLimitGb);
        StorageLimitGb = gigabytes;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SetSimultaneousDownloadsAsync(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, DownloadPreferenceLimits.MinSimultaneous);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, DownloadPreferenceLimits.MaxSimultaneous);
        SimultaneousDownloads = count;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public event EventHandler? Changed;

    public Task LatestSave => Task.CompletedTask;

    public Task SetFolderAsync(string folder)
    {
        Folder = folder;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SetDownloadsOnMeteredAsync(bool allowed)
    {
        DownloadsOnMetered = allowed;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SetKeepWatchedAsync(bool keep)
    {
        KeepWatched = keep;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SetNotifyWhenFinishedAsync(bool notify)
    {
        NotifyWhenFinished = notify;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SetDefaultQualityAsync(string quality)
    {
        DefaultQuality = quality;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}
