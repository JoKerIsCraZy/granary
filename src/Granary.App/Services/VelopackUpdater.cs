using Granary.Core.Abstractions;
using Granary.Core.Updates;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Granary.App.Services;

/// <summary>
/// Granary's updates through Velopack (docs/release.md): releases on the GitHub repository the build names
/// (<see cref="BuildInfo.UpdateRepository"/>), downloaded in the background, applied by Velopack's updater once Granary has
/// exited. Only a release signed with the key the app carries is downloaded and applied (<see cref="SignedReleaseVerifier"/>).
/// A copy not installed by Granary's setup, or built without a repository or a key, does not update.
/// </summary>
internal sealed class VelopackUpdater : IAppUpdater
{
    private readonly SignedReleaseVerifier _verifier;
    private readonly UpdateManager? _manager;
    private readonly IVelopackLocator? _locator;
    private readonly Uri? _repository;
    private UpdateInfo? _found;
    private (UpdateInfo Update, SignedPackage Package)? _ready;

    public VelopackUpdater(SignedReleaseVerifier verifier, ILogger<VelopackUpdater> logger)
    {
        _verifier = verifier;
        if (BuildInfo.UpdateRepository is not { } repository)
        {
            logger.LogInformation("Updates are off: this build names no release repository");
            Support = AppUpdateSupport.NoReleaseSource;
            return;
        }
        if (!UpdateSigningKey.Embedded.IsPresent)
        {
            // pack-release.ps1 refuses such a build; one made another way still trusts no update.
            logger.LogWarning("Updates are off: this build carries no update signing key");
            Support = AppUpdateSupport.NoReleaseSource;
            return;
        }
        var locator = VelopackLocator.Current;
        var options = new UpdateOptions { MaximumDeltasBeforeFallback = UpdateSafety.MaximumDeltas };
        var manager = new UpdateManager(new GithubSource(repository.AbsoluteUri, accessToken: null, prerelease: false), options, locator);
        if (!manager.IsInstalled)
        {
            logger.LogInformation("Updates are off: this copy was not installed by Granary's setup");
            Support = AppUpdateSupport.NotInstalled;
            return;
        }
        _manager = manager;
        _locator = locator;
        _repository = repository;
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
        // The feed's notes are not signed; the ones shown come from the verified package.
        return update is null ? null : new AppRelease(update.TargetFullRelease.Version.ToString(), Notes: null);
    }

    public async Task<AppRelease?> DownloadAsync(IProgress<int>? progress, CancellationToken cancellationToken)
    {
        _ready = null;
        if (_manager is not { AppId: { } appId } manager || _locator?.PackagesDir is not { } packages || _repository is not { } repository
            || _found is not { } update)
        {
            return null;
        }
        var target = update.TargetFullRelease;
        string version = target.Version.ToString();
        var feed = new FeedPackage(version, target.FileName, target.SHA256);
        var signed = await _verifier.DownloadAsync(repository, appId, feed, packages,
            token => manager.DownloadUpdatesAsync(update, percent => progress?.Report(percent), token), cancellationToken);
        if (signed is not { Verdict: UpdateVerdict.Verified, Package: { } package })
        {
            return null;
        }
        _ready = (update, package);
        return new AppRelease(version, signed.Notes);
    }

    public void ApplyWhenExited(bool restart)
    {
        // Checked once more as Granary closes: Velopack's updater applies the file it finds then.
        if (_manager is { } manager && _ready is { } ready && _verifier.IsUnchanged(ready.Package))
        {
            manager.WaitExitThenApplyUpdates(ready.Update.TargetFullRelease, silent: true, restart: restart);
        }
    }
}
