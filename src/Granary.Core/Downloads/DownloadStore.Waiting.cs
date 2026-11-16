namespace Granary.Core.Downloads;

/// <summary>
/// The episodes waiting for the server's limit of downloads (<see cref="WaitingEpisode"/>), in the index beside the downloads, so
/// they hold across starts and go with their account at a sign-out (<see cref="RemoveWhereAsync"/>). An episode waits once per
/// profile, and not once it is kept here: a download of it, however it came, takes it out of the line.
/// </summary>
public sealed partial class DownloadStore
{
    /// <summary>The scope's waiting episodes, in the order they are asked for again.</summary>
    public IReadOnlyList<WaitingEpisode> WaitingIn(DownloadScope scope) => [.. Current.Waiting.Where(w => w.Scope == scope)];

    /// <summary>The scope's episode <paramref name="episodeId"/> waits: it is on its way to this PC as a download of it would be.</summary>
    public bool IsWaiting(DownloadScope scope, string episodeId) => Current.Waiting.Any(w => w.Scope == scope && w.EpisodeId == episodeId);

    /// <summary>
    /// Puts <paramref name="episodes"/> in line after the ones waiting already, leaving out one waiting already or kept here, and
    /// all of a scope whose downloads were cleared since <paramref name="clearGeneration"/> (the run that refused them began
    /// before a sign-out). Answers how many were put in line.
    /// </summary>
    public async Task<int> AddWaitingUnlessClearedAsync(IReadOnlyList<WaitingEpisode> episodes, long clearGeneration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        await LoadAsync(cancellationToken);
        int added = 0;
        Change(state =>
        {
            var line = new List<WaitingEpisode>(state.Waiting);
            foreach (var episode in episodes)
            {
                if (IsClearedSinceLocked(episode.Scope, clearGeneration) || line.Any(w => IsSame(w, episode))
                    || state.Downloads.Any(d => d.Record.Scope == episode.Scope && d.Record.MediaItemId == episode.EpisodeId))
                {
                    continue;
                }
                line.Add(episode with { ClearsSeen = _clearGeneration });
                added++;
            }
            return added == 0 ? state : state with { Waiting = line };
        });
        if (added > 0)
        {
            await RaiseThenWriteIndexAsync();
        }
        return added;
    }

    /// <summary>Takes the scope's waiting episodes <paramref name="match"/> picks out of line; answers how many went.</summary>
    public async Task<int> RemoveWaitingAsync(DownloadScope scope, Func<WaitingEpisode, bool> match, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(match);
        await LoadAsync(cancellationToken);
        int removed = 0;
        Change(state =>
        {
            List<WaitingEpisode> kept = [.. state.Waiting.Where(w => w.Scope != scope || !match(w))];
            removed = state.Waiting.Count - kept.Count;
            return removed == 0 ? state : state with { Waiting = kept };
        });
        if (removed > 0)
        {
            await RaiseThenWriteIndexAsync();
        }
        return removed;
    }

    /// <summary>Puts the scope's waiting episode <paramref name="episodeId"/> at the end of the line; none there is no change.</summary>
    public async Task MoveWaitingToEndAsync(DownloadScope scope, string episodeId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await LoadAsync(cancellationToken);
        bool moved = false;
        Change(state =>
        {
            var episode = state.Waiting.FirstOrDefault(w => w.Scope == scope && w.EpisodeId == episodeId);
            moved = episode is not null;
            return moved ? state with { Waiting = [.. state.Waiting.Where(w => w != episode), episode!] } : state;
        });
        if (moved)
        {
            await RaiseThenWriteIndexAsync();
        }
    }

    private static bool IsSame(WaitingEpisode first, WaitingEpisode second) => first.Scope == second.Scope && first.EpisodeId == second.EpisodeId;

    // A change of the line: the followers hear first, then the index is written. A handler that throws does not keep the change
    // from being written; its exception comes after.
    private async Task RaiseThenWriteIndexAsync()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            await PersistAsync(() => WriteIndex(Current));
        }
    }
}
