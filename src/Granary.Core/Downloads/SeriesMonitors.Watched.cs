using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>Delete after watching, as Silo's Apple app does it (<c>retentionDeletions</c>, <c>watchedDownloadIds</c>).</summary>
public sealed partial class SeriesMonitors
{
    // At the end of a run: the server hears what was watched on this PC and its progress is read back, then the whole episodes
    // of a series whose monitor deletes watched ones, which the profile has watched for sure (DownloadRecord.IsWatched), go from
    // this PC, each as Delete does it, the server told. Not one that plays now; and, as what is deleted is a series' episodes,
    // only a series all of whose monitors delete watched ones, none of them one this run could not be sure of
    // (<paramref name="unsure"/>); the server's sync, for its part, registers no episode the profile finished, and none this
    // PC deleted while the monitor was there.
    private async Task DeleteWatchedAsync(DownloadScope scope, IReadOnlySet<string> unsure, CancellationToken cancellationToken)
    {
        var series = _book.In(scope).GroupBy(m => m.SeriesId, StringComparer.Ordinal)
            .Where(g => g.All(m => m.DeleteWatched && !unsure.Contains(m.Id)))
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        if (series.Count == 0)
        {
            return;
        }
        if (_progress is not null)
        {
            // Its runs never throw: what they could not send or read stays as it was, and a watched mark is only ever the
            // server's word or a play to the end here.
            await _progress.FlushAsync(readBack: true, cancellationToken).ConfigureAwait(false);
        }
        if (!IsStill(scope))
        {
            return;
        }
        string[] watched = [.. _store.In(scope)
            .Where(d => d.Record is { State: DownloadState.Completed, IsWatched: true, SeriesId: { } id } && series.Contains(id))
            .Select(d => d.Record.DownloadId)];
        if (watched.Length == 0)
        {
            return;
        }
        try
        {
            int deleted = await _manager.DeleteWatchedAsync(scope, watched, cancellationToken).ConfigureAwait(false);
            if (deleted > 0)
            {
                _logger.LogInformation("{Count} watched episodes of monitored series were deleted from this PC", deleted);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The index could not be written: what was not deleted is tried again with the next run.
            _logger.LogWarning("Watched episodes of monitored series could not be deleted ({Failure})", DownloadLog.Failure(ex));
        }
    }
}
