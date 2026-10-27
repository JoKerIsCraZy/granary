namespace Granary.Core.Abstractions;

/// <summary>Whether this copy of Granary can update itself, and why not.</summary>
public enum AppUpdateSupport
{
    /// <summary>Installed by Granary's setup, and built knowing where releases are.</summary>
    Supported,

    /// <summary>Built without a release repository: a development build.</summary>
    NoReleaseSource,

    /// <summary>Not installed by Granary's setup: run from where it was built.</summary>
    NotInstalled,
}

/// <summary>A newer release of Granary.</summary>
/// <param name="Notes">What changed, in Markdown, as the release was packed with; null without.</param>
public sealed record AppRelease(string Version, string? Notes);

/// <summary>
/// Updates Granary itself: a copy installed by its setup looks for newer releases where they are published, downloads one
/// in the background and applies it once the app has exited (docs/release.md). The app's is Velopack's; without one (a
/// development build, tests) nothing updates.
/// </summary>
public interface IAppUpdater
{
    AppUpdateSupport Support { get; }

    /// <summary>Looks for a newer release than this copy; null when there is none.</summary>
    Task<AppRelease?> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Downloads the release <see cref="CheckAsync"/> found, telling how far it is in percent.</summary>
    Task DownloadAsync(IProgress<int>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Has the downloaded release applied once this process has exited, and Granary started again afterwards when
    /// <paramref name="restart"/>. Does nothing without one.
    /// </summary>
    void ApplyWhenExited(bool restart);
}

/// <summary>The updater of a copy that cannot update itself.</summary>
public sealed class NoAppUpdater : IAppUpdater
{
    public AppUpdateSupport Support => AppUpdateSupport.NoReleaseSource;

    public Task<AppRelease?> CheckAsync(CancellationToken cancellationToken) => Task.FromResult<AppRelease?>(null);

    public Task DownloadAsync(IProgress<int>? progress, CancellationToken cancellationToken) => Task.CompletedTask;

    public void ApplyWhenExited(bool restart)
    {
    }
}
