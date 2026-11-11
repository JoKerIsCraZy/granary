using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>
/// Clears: a sign-out's mark that nothing more is kept or started for the downloads of an account on a server, or of every
/// account there for a server's removal. A create that began before the mark keeps nothing when its answer comes
/// (<see cref="AddUnlessClearedAsync"/>), and the queue starts no transfer and no server work for those downloads until their
/// scope becomes the active one again after the sign-out (<see cref="IsClearedSince"/>), or the sign-out ends with the account
/// still signed in (<see cref="EndClear"/>). Marks hold for this run.
/// </summary>
public sealed partial class DownloadStore
{
    private readonly List<Clear> _clears = [];
    private long _clearGeneration;

    /// <summary>Raised once a sign-out's clear ended (<see cref="EndClear"/>), on the thread that ended it.</summary>
    internal event EventHandler? ClearEnded;

    /// <summary>How many clears this run has seen. A create takes it as it begins, the queue as a scope becomes the active one.</summary>
    public long ClearGeneration
    {
        get
        {
            lock (_gate)
            {
                return _clearGeneration;
            }
        }
    }

    /// <summary>
    /// From now on nothing more is kept or started for the downloads of a server's account (null: of every account there). A
    /// sign-out marks them before it takes them off this PC, and ends the mark once it was tried (<see cref="EndClear"/>).
    /// Answers the new <see cref="ClearGeneration"/>, which names this clear.
    /// </summary>
    public long MarkCleared(string serverEntryId, string? accountId)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverEntryId);
        lock (_gate)
        {
            _clears.Add(new Clear(++_clearGeneration, serverEntryId, accountId, IsPending: true));
            return _clearGeneration;
        }
    }

    /// <summary>
    /// The sign-out of the clear <paramref name="generation"/> names is over, whether it signed the account out or failed. A
    /// create that began before the clear still keeps nothing; after a sign-out that failed, the queue runs the account's
    /// downloads again, new ones included. Twice is no change.
    /// </summary>
    public void EndClear(long generation)
    {
        lock (_gate)
        {
            int index = _clears.FindIndex(c => c.Generation == generation && c.IsPending);
            if (index < 0)
            {
                return;
            }
            _clears[index] = _clears[index] with { IsPending = false };
        }
        ClearEnded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether a sign-out that takes the downloads of <paramref name="scope"/> along is under way: marked, and not ended yet.</summary>
    public bool IsClearPending(DownloadScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            return _clears.Any(c => c.IsPending && c.Takes(scope));
        }
    }

    /// <summary>Whether a clear after <paramref name="generation"/> (a <see cref="ClearGeneration"/>) took the downloads of <paramref name="scope"/>.</summary>
    public bool IsClearedSince(DownloadScope scope, long generation)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            return IsClearedSinceLocked(scope, generation);
        }
    }

    private bool IsClearedSinceLocked(DownloadScope scope, long generation) => _clears.Any(c => c.Generation > generation && c.Takes(scope));

    // A new folder whose download is not kept after all, as one cleared while it was made: it goes again, or waits to be
    // deleted as a removed download's folder.
    private void Unmake(StoredDownload download)
    {
        try
        {
            Directory.Delete(download.Folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The new folder of download {DownloadId} could not be deleted yet ({Failure}); it is tried again",
                download.Record.DownloadId, DownloadLog.Failure(ex));
            Change(state => state with
            {
                FolderDeletes = [.. state.FolderDeletes, new FolderDelete(download.Record.Scope, download.Record.DownloadId, download.Folder)],
            });
            WriteIndex(Current);
        }
    }

    /// <summary>
    /// A sign-out's mark: the downloads of an account on a server (null: of every account there), after a generation; pending
    /// until the sign-out was tried.
    /// </summary>
    private sealed record Clear(long Generation, string ServerEntryId, string? AccountId, bool IsPending)
    {
        public bool Takes(DownloadScope scope) => ServerEntryId == scope.ServerEntryId && (AccountId is null || AccountId == scope.AccountId);
    }
}
