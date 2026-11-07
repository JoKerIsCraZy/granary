using Granary.Core.Api.Downloads;
using Granary.Core.Downloads;

namespace Granary.Tests.Fakes;

/// <summary>This PC's download settings without a file; each change is raised at once, as the real one does.</summary>
internal sealed class InMemoryDownloadPreferences(string folder) : IDownloadPreferences
{
    public string Folder { get; private set; } = folder;

    public bool DownloadsOnMetered { get; private set; }

    public string DefaultQuality { get; private set; } = DownloadQualities.Original;

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

    public Task SetDefaultQualityAsync(string quality)
    {
        DefaultQuality = quality;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}
