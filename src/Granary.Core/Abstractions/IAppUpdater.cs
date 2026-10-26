namespace Granary.Core.Abstractions;

/// <summary>
/// Updates Granary itself: a copy installed by its setup looks for newer releases where they are published, downloads one
/// in the background and applies it once the app has exited (docs/release.md). The app's is Velopack's; without one (a
/// development build, tests) nothing updates.
/// </summary>
public interface IAppUpdater
{
    /// <summary>Whether this copy can update itself: installed by Granary's setup, and built knowing where releases are.</summary>
    bool CanUpdate { get; }

    /// <summary>Looks for a newer release and downloads it; its version once it is downloaded, null when there is none.</summary>
    Task<string?> CheckAndDownloadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Has the downloaded release applied once this process has exited, and Granary started again afterwards when
    /// <paramref name="restart"/>. Does nothing without one.
    /// </summary>
    void ApplyWhenExited(bool restart);
}

/// <summary>The updater of a copy that cannot update itself.</summary>
public sealed class NoAppUpdater : IAppUpdater
{
    public bool CanUpdate => false;

    public Task<string?> CheckAndDownloadAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public void ApplyWhenExited(bool restart)
    {
    }
}
