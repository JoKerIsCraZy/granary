namespace Granary.Core.Downloads;

/// <summary>
/// The downloads a player of this app has open now. A play from this PC has no server session that would say so, yet what
/// deletes downloads by itself must not take the file from under the player, which stays up, minimized, while the Downloads
/// page is used: Silo's Android app keeps the same mark for its Reclaim Watched (<c>ActivePlaybackFile</c>).
/// </summary>
/// <remarks>Safe from any thread. <see cref="Changed"/> is raised on the thread that started or ended the play.</remarks>
public sealed class PlayingDownloads
{
    private readonly Lock _gate = new();
    private readonly List<(DownloadScope Scope, string DownloadId)> _playing = [];

    /// <summary>A download started or stopped playing.</summary>
    public event EventHandler? Changed;

    /// <summary>A player has this download open.</summary>
    public bool Contains(DownloadScope scope, string downloadId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            return _playing.Contains((scope, downloadId));
        }
    }

    /// <summary>Counts the download as playing until the returned handle is disposed; twice is no change.</summary>
    public IDisposable Enter(DownloadScope scope, string downloadId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrEmpty(downloadId);
        lock (_gate)
        {
            _playing.Add((scope, downloadId));
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return new Play(this, (scope, downloadId));
    }

    private void Leave((DownloadScope Scope, string DownloadId) play)
    {
        lock (_gate)
        {
            _playing.Remove(play);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Play(PlayingDownloads owner, (DownloadScope Scope, string DownloadId) play) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Leave(play);
            }
        }
    }
}
