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

    // Both functions run under the lock, where they also set the timer that reads the list again.
    private Reread PreparationsSource() => Source(EventsChannels.DownloadPreparations, "download preparations",
        async (server, cancellationToken) =>
        {
            var list = await _admin.ListDownloadPreparationsAsync(server, cancellationToken).ConfigureAwait(false);
            return state =>
            {
                _preparationsDue.Change(list.Counts.Active > 0 ? PreparationsRefresh : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return state with { Preparations = list };
            };
        },
        state =>
        {
            _preparationsDue.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return state with { Preparations = null };
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
        if (_state.Preparations is not { } list
            || Payload(frame.Data, AdminJsonContext.Default.AdminDownloadPreparationReading) is not { Id: { Length: > 0 } id, Progress: { } progress })
        {
            return false;
        }
        var items = list.Items;
        for (int index = 0; index < items.Count; index++)
        {
            if (items[index].Id != id)
            {
                continue;
            }
            if (!items[index].IsRunning)
            {
                return false;
            }
            _state = _state with
            {
                Preparations = list with { Items = [.. items.Take(index), items[index] with { Progress = progress }, .. items.Skip(index + 1)] },
            };
            return true;
        }
        return false;
    }
}
