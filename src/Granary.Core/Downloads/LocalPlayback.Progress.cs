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

    // Left past this much of its length, a title starts over next time, as one the server counts as watched does (the Apple
    // app's defaultWatchedFraction, and the server's default threshold). Whether it is watched is the server's to say: an
    // admin can set another threshold (playback.watched_threshold).
    private const double StartsOverFraction = 0.9;

    private readonly DownloadStore? _store;
    private readonly OfflineProgressQueue? _progress;
    private readonly OfflineProgressSync? _sync;
    private ITimer? _progressTimer;

    // The file showed a frame in this playback: before that, nothing is the viewer's position.
    private bool _played;

    // The position recorded last; a pause records nothing new, so a title left paused does not grow newer by itself.
    private double? _recorded;

    // When it was recorded, as it was stamped.
    private DateTimeOffset? _recordedAt;

    // It was recorded as a title left near its end: the next play starts over.
    private bool _recordedStartsOver;
    private double? _engineDuration;

    // The record's writes, in the order the positions were recorded.
    private Task _records = Task.CompletedTask;

    // The store's clears as the play began: a sign-out that takes the account's downloads along since then ends the recording.
    private readonly long _clearsSeen;

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
    /// whether it now counts the title as watched. <paramref name="brokeOff"/>: the file would not play on from here, so
    /// here is where Retry and the next play go on, however near the end it is.
    /// </summary>
    private void RecordProgress(bool isFinal, bool playedToTheEnd = false, bool brokeOff = false)
    {
        if (_progress is not { } queue || _stopping || !_played)
        {
            return;
        }
        if (_store?.IsClearedSince(_download.Record.Scope, _clearsSeen) == true)
        {
            // The account is being signed out with its downloads, or its server removed: what waited to be sent went with
            // them (DownloadSignOut), and the player that closes now adds nothing again.
            return;
        }
        double position = _position;
        // Nothing new while the position stands, but for the title's end, and for a file that breaks off where its position
        // was just recorded as one left near the end: that record would have the next play start over.
        bool stands = _recorded is { } last && Math.Abs(position - last) < 1 && !playedToTheEnd && !(brokeOff && _recordedStartsOver);
        if (!double.IsFinite(position) || position <= 0 || stands)
        {
            return;
        }
        _recorded = position;
        var record = _download.Record;
        double duration = DurationSeconds ?? _engineDuration ?? 0;
        long positionMs = (long)Math.Round(position * 1000);
        bool startsOver = playedToTheEnd || (!brokeOff && duration > 0 && position / duration > StartsOverFraction);
        _recordedStartsOver = startsOver;
        // Later than what this PC last recorded for the title or read of it from the server, also on a clock that was set
        // back meanwhile: the viewer is here now.
        var known = _store?.Find(record.Scope, record.DownloadId)?.Record.ResumeAt;
        var entry = queue.Record(record.Scope, record.MediaItemId, positionMs, (long)Math.Round(Math.Max(0, duration) * 1000),
            after: _recordedAt > known ? _recordedAt : known ?? _recordedAt);
        _recordedAt = entry.UpdatedAt;
        // As the server sets a watched title's position back to 0.
        long resumeMs = startsOver ? 0 : positionMs;
        // Watched for sure only at the title's end. Any other position is a watch under way, also of a title watched
        // before: it is not offered for deletion in the middle of it. The server's word follows with the next read.
        // A write that comes late, behind a slow disk, never lowers a newer position the server's read stored meanwhile.
        _records = KeepAsync(_records, r => r.ResumeAt is { } kept && kept > entry.UpdatedAt
            ? r
            : r with { ResumeMs = resumeMs, ResumeAt = entry.UpdatedAt, IsWatched = playedToTheEnd });
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
