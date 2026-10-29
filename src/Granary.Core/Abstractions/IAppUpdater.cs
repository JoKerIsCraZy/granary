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
/// <param name="Notes">
/// What changed, in Markdown, as the release's signed package carries it; null without, and null before the download is
/// verified. Notes from the unsigned feed are never shown.
/// </param>
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

    /// <summary>
    /// Downloads the release <see cref="CheckAsync"/> found, telling how far it is in percent. The release as verified,
    /// with the notes its signed package carries, once it is ready to apply; null when it could not be verified, and then
    /// nothing is applied.
    /// </summary>
    Task<AppRelease?> DownloadAsync(IProgress<int>? progress, CancellationToken cancellationToken);

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

    public Task<AppRelease?> DownloadAsync(IProgress<int>? progress, CancellationToken cancellationToken) => Task.FromResult<AppRelease?>(null);

    public void ApplyWhenExited(bool restart)
    {
    }
}
