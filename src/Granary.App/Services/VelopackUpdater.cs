using Granary.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Granary.App.Services;

/// <summary>
/// Granary's updates through Velopack (docs/release.md): releases on the GitHub repository the build names
/// (<see cref="BuildInfo.UpdateRepository"/>), downloaded in the background, applied by Velopack's updater once Granary has
/// exited. A copy not installed by Granary's setup, or built without a repository, does not update.
/// </summary>
internal sealed class VelopackUpdater : IAppUpdater
{
    private readonly UpdateManager? _manager;
    private UpdateInfo? _found;
    private UpdateInfo? _ready;

    public VelopackUpdater(ILogger<VelopackUpdater> logger)
    {
        if (BuildInfo.UpdateRepository is not { } repository)
        {
            logger.LogInformation("Updates are off: this build names no release repository");
            Support = AppUpdateSupport.NoReleaseSource;
            return;
        }
        var manager = new UpdateManager(new GithubSource(repository.AbsoluteUri, accessToken: null, prerelease: false));
        if (!manager.IsInstalled)
        {
            logger.LogInformation("Updates are off: this copy was not installed by Granary's setup");
            Support = AppUpdateSupport.NotInstalled;
            return;
        }
        _manager = manager;
        Support = AppUpdateSupport.Supported;
        logger.LogInformation("Granary {Version} looks for updates in {Repository}", manager.CurrentVersion, repository);
    }

    public AppUpdateSupport Support { get; }

    public async Task<AppRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        if (_manager is not { } manager)
        {
            return null;
        }
        var update = await manager.CheckForUpdatesAsync();
        cancellationToken.ThrowIfCancellationRequested();
        _found = update;
        if (update is null)
        {
            return null;
        }
        var release = update.TargetFullRelease;
        return new AppRelease(release.Version.ToString(), string.IsNullOrWhiteSpace(release.NotesMarkdown) ? null : release.NotesMarkdown);
    }

    public async Task DownloadAsync(IProgress<int>? progress, CancellationToken cancellationToken)
    {
        if (_manager is not { } manager || _found is not { } update)
        {
            return;
        }
        await manager.DownloadUpdatesAsync(update, percent => progress?.Report(percent), cancellationToken);
        _ready = update;
    }

    public void ApplyWhenExited(bool restart)
    {
        if (_manager is { } manager && _ready is { } update)
        {
            manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: restart);
        }
    }
}
