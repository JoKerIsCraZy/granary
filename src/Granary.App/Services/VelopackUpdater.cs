using System.Reflection;
using Granary.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Granary.App.Services;

/// <summary>
/// Granary's updates through Velopack (docs/release.md): releases on the GitHub repository the build names
/// (<c>GranaryUpdateRepository</c>), downloaded in the background, applied by Velopack's updater once Granary has exited.
/// A copy not installed by Granary's setup, or built without a repository, does not update.
/// </summary>
internal sealed class VelopackUpdater : IAppUpdater
{
    private readonly UpdateManager? _manager;
    private UpdateInfo? _ready;

    public VelopackUpdater(ILogger<VelopackUpdater> logger)
    {
        string? repository = typeof(VelopackUpdater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "GranaryUpdateRepository")?.Value;
        if (string.IsNullOrWhiteSpace(repository))
        {
            logger.LogInformation("Updates are off: this build names no release repository");
            return;
        }
        var manager = new UpdateManager(new GithubSource(repository, accessToken: null, prerelease: false));
        if (!manager.IsInstalled)
        {
            logger.LogInformation("Updates are off: this copy was not installed by Granary's setup");
            return;
        }
        _manager = manager;
        logger.LogInformation("Granary {Version} looks for updates in {Repository}", manager.CurrentVersion, repository);
    }

    public bool CanUpdate => _manager is not null;

    public async Task<string?> CheckAndDownloadAsync(CancellationToken cancellationToken)
    {
        if (_manager is not { } manager)
        {
            return null;
        }
        var update = await manager.CheckForUpdatesAsync();
        if (update is null)
        {
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        await manager.DownloadUpdatesAsync(update, progress: null, cancelToken: cancellationToken);
        _ready = update;
        return update.TargetFullRelease.Version.ToString();
    }

    public void ApplyWhenExited(bool restart)
    {
        if (_manager is { } manager && _ready is { } update)
        {
            manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: restart);
        }
    }
}
