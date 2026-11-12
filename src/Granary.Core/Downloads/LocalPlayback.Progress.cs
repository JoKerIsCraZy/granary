using Microsoft.Extensions.Logging;

namespace Granary.Core.Downloads;

// Where the viewer is in a download (docs/architecture.md, "Offline progress"). No server session reports it, so the
// playback records it itself, as Silo's Apple app does (PlayerViewModel.recordOfflineProgress): every 10 seconds while
// the position moves, and at the stop, the end and a failure. Each record goes into the queue the server is sent from
// (OfflineProgressQueue), which sends at once while the server can be reached, and into the download's own record, which
// the next play from this PC resumes from.
public sealed partial class LocalPlayback
{
    /// <summary>How often the position is recorded while the title plays.</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);

    // Past this much of its length a title counts as watched on this PC until the server says (the Apple app's
    // defaultWatchedFraction, and the server's default threshold).
    private const double WatchedFraction = 0.9;

    private readonly DownloadStore? _store;
    private readonly OfflineProgressQueue? _progress;
    private readonly OfflineProgressSync? _sync;
    private ITimer? _progressTimer;

    // The file showed a frame in this playback: before that, nothing is the viewer's position.
    private bool _played;

    // The position recorded last; a pause records nothing new, so a title left paused does not grow newer by itself.
    private double? _recorded;
    private double? _engineDuration;

    // The record's writes, in the order the positions were recorded.
    private Task _records = Task.CompletedTask;

    // This PC holds a position of the title the server has not answered for: it is newer than what the server says.
    private bool HasUnsentProgress => _progress?.HasUnsent(_download.Record.Scope, _download.Record.MediaItemId) == true;

    private void StartProgress() =>
        _progressTimer ??= _time.CreateTimer(_ => _dispatcher.Post(() => RecordProgress(isFinal: false)), null, ProgressInterval, ProgressInterval);

    private void StopProgress()
    {
        _progressTimer?.Dispose();
        _progressTimer = null;
    }

    /// <summary>
    /// Records where the viewer is, unless nothing played yet or the position has not moved since the last record.
    /// <paramref name="isFinal"/>: the play ends here, so the server's progress is read back after the send, to learn
    /// whether it now counts the title as watched.
    /// </summary>
    private void RecordProgress(bool isFinal, bool playedToTheEnd = false)
    {
        if (_progress is not { } queue || _stopping || !_played)
        {
            return;
        }
        double position = _position;
        if (!double.IsFinite(position) || position <= 0 || (_recorded is { } last && Math.Abs(position - last) < 1 && !playedToTheEnd))
        {
            return;
        }
        _recorded = position;
        var record = _download.Record;
        double duration = DurationSeconds ?? _engineDuration ?? 0;
        long positionMs = (long)Math.Round(position * 1000);
        bool watched = playedToTheEnd || (duration > 0 && position / duration > WatchedFraction);
        var entry = queue.Record(record.Scope, record.MediaItemId, positionMs, (long)Math.Round(Math.Max(0, duration) * 1000));
        // A watched title starts over next time, as the server sets its position back to 0.
        long resumeMs = watched ? 0 : positionMs;
        _records = KeepAsync(_records, r => r with { ResumeMs = resumeMs, ResumeAt = entry.UpdatedAt, IsWatched = r.IsWatched || watched });
        _sync?.FlushSoon(readBack: isFinal);
    }

    private async Task KeepAsync(Task previous, Func<DownloadRecord, DownloadRecord> change)
    {
        await previous.ConfigureAwait(false);
        if (_store is null)
        {
            return;
        }
        try
        {
            await _store.UpdateAsync(_download.Record.Scope, _download.Record.DownloadId, change, CancellationToken.None).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            // Deleted while it played.
        }
        catch (Exception ex)
        {
            // Nobody waits for it but the stop: the position is in the queue either way.
            _logger.LogWarning("Download {DownloadId} could not keep its position ({Failure})", _download.Record.DownloadId, DownloadLog.Failure(ex));
        }
    }
}
