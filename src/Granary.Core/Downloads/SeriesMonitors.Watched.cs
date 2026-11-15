using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

/// <summary>Delete after watching, as Silo's Apple app does it (<c>retentionDeletions</c>, <c>watchedDownloadIds</c>).</summary>
public sealed partial class SeriesMonitors
{
    // At the end of a run: the server hears what was watched on this PC and its progress is read back, then the whole episodes
    // of a series whose monitor deletes watched ones, which the profile has watched for sure (DownloadRecord.IsWatched), go from
    // this PC, each as Delete does it, the server told. Not one that plays now; the server's sync, for its part, registers no
    // episode the profile finished, and none this PC deleted while the monitor was there.
    private async Task DeleteWatchedAsync(DownloadScope scope, CancellationToken cancellationToken)
    {
        var series = _book.In(scope).Where(m => m.DeleteWatched).Select(m => m.SeriesId).ToHashSet(StringComparer.Ordinal);
        if (series.Count == 0)
        {
            return;
        }
        if (_progress is not null)
        {
            try
            {
                await _progress.FlushAsync(readBack: true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // What was read before stands: a watched mark is only ever the server's word or a play to the end here.
                _logger.LogInformation("The progress could not be read before Delete after watching ({Failure})", ex.GetType().Name);
            }
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
        int deleted = await _manager.DeleteWatchedAsync(scope, watched, cancellationToken).ConfigureAwait(false);
        if (deleted > 0)
        {
            _logger.LogInformation("{Count} watched episodes of monitored series were deleted from this PC", deleted);
        }
    }
}
