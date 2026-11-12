using Granary.Core.Api.Admin;

namespace Granary.Core.Realtime;

// The queue that prepares offline downloads, as the web keeps it (hooks/queries/admin/downloadPreparations.ts and
// RealtimeEventsProvider.tsx): the list is read once the server gave the channel and on every event but two. A progress
// reading patches its running job in place, and download_storage.changed is news of the storage views, which Granary
// does not show.
public sealed partial class ServerActivity
{
    /// <summary>
    /// While jobs run, wait or retry, the list is read again this long after its last read: not every change to a job
    /// announces itself, as the web notes.
    /// </summary>
    public static readonly TimeSpan PreparationsRefresh = TimeSpan.FromSeconds(60);

    private const string PreparationProgress = EventsFrames.PreparationProgress;
    private const string StorageChanged = "download_storage.changed";

    // The readings that came while the list was being read, by job: the answer was made before them and would step
    // their jobs back until the next reading, five seconds later.
    private readonly Dictionary<string, AdminDownloadPreparationProgress> _readingsDuringRead = new(StringComparer.Ordinal);

    // The three functions run under the lock, where they also set the timer that reads the list again.
    private Reread PreparationsSource() => Source(EventsChannels.DownloadPreparations, "download preparations",
        async (server, cancellationToken) =>
        {
            var list = await _admin.ListDownloadPreparationsAsync(server, cancellationToken).ConfigureAwait(false);
            return state =>
            {
                _preparationsDue.Change(list.Counts.Active > 0 ? PreparationsRefresh : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                var shown = WithReadingsLocked(list);
                _readingsDuringRead.Clear();
                return state with { Preparations = shown };
            };
        },
        state =>
        {
            _preparationsDue.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _readingsDuringRead.Clear();
            return state with { Preparations = null };
        },
        () =>
        {
            // The read that failed was also the one that would have set the next: while jobs were on their way when
            // the list was last read, it is tried again as if it had answered.
            _readingsDuringRead.Clear();
            if (_state.Preparations is { Counts.Active: > 0 })
            {
                _preparationsDue.Change(PreparationsRefresh, Timeout.InfiniteTimeSpan);
            }
        });

    private void OnPreparationsDue()
    {
        lock (_gate)
        {
            CueLocked(_preparations);
        }
    }

    // True when the state changed with the event itself.
    private bool PreparationEventLocked(EventsFrame frame)
    {
        if (frame.Event == StorageChanged)
        {
            return false;
        }
        if (frame.Event == PreparationProgress && !frame.IsCut && ProgressLocked(frame))
        {
            return true;
        }
        CueLocked(_preparations);
        return false;
    }

    // A reading for a job that runs in the list as it is known; anything else is for a read to put right.
    private bool ProgressLocked(EventsFrame frame)
    {
        if (Payload(frame.Data, AdminJsonContext.Default.AdminDownloadPreparationReading) is not { Id: { Length: > 0 } id, Progress: { } progress })
        {
            return false;
        }
        if (_preparations.IsReading)
        {
            _readingsDuringRead[id] = progress;
        }
        if (_state.Preparations is not { } list || WithReading(list, id, progress) is not { } patched)
        {
            return false;
        }
        _state = _state with { Preparations = patched };
        return true;
    }

    // The list with the reading in its running job; null when the job is not in it or does not run.
    private static AdminDownloadPreparationList? WithReading(AdminDownloadPreparationList list, string id, AdminDownloadPreparationProgress progress)
    {
        var items = list.Items;
        for (int index = 0; index < items.Count; index++)
        {
            if (items[index].Id == id)
            {
                return items[index].IsRunning
                    ? list with { Items = [.. items.Take(index), items[index] with { Progress = progress }, .. items.Skip(index + 1)] }
                    : null;
            }
        }
        return null;
    }

    private AdminDownloadPreparationList WithReadingsLocked(AdminDownloadPreparationList list)
    {
        foreach (var (id, progress) in _readingsDuringRead)
        {
            list = WithReading(list, id, progress) ?? list;
        }
        return list;
    }
}
