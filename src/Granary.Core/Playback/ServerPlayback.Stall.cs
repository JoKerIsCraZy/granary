using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

// A stream that waits for data while the viewer plays is given up and reconnected, as Android gives a stalled load 90 s
// (docs/architecture.md, Stalls).
public sealed partial class ServerPlayback
{
    // Above the server's longest hold of a request and the media proxy's 60 s, after which mpv asks again by itself.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(90);

    // Seconds of the title mpv must gain for data to count as still arriving: a slow link is not a lost one.
    private const double StallGrowth = 0.5;

    // mpv waits for data (paused-for-cache) in the load that plays, since _bufferingSince.
    private bool _buffering;
    private long _bufferingSince;

    // Where what mpv holds from the position ends, in player time, by the latest state.
    private double _heldTo;

    // The wait for data while the viewer wants to play, timed from where it began and from what mpv held then; each new
    // one has its number.
    private ITimer? _stallWatch;
    private int _stallWatches;
    private double _stallAt;
    private double _stallHeldTo;

    private void FollowBuffering(PlaybackSnapshot state)
    {
        _heldTo = state.Position.TotalSeconds + HeldAhead(state);
        bool buffering = state is { IsBuffering: true, IsLoaded: true };
        if (buffering != _buffering)
        {
            _buffering = buffering;
            if (buffering)
            {
                _bufferingSince = _time.GetTimestamp();
                _logger.LogInformation("Buffering at {Position:F1} s, {Held:F1} s held ahead", SourcePosition, HeldAhead(state));
            }
            else
            {
                _logger.LogInformation("Buffering ended at {Position:F1} s after {Seconds:F1} s", SourcePosition,
                    _time.GetElapsedTime(_bufferingSince).TotalSeconds);
            }
        }
        WatchStall();
    }

    // Counts while the viewer wants the picture after the first frame; a new position or new data starts it again.
    private void WatchStall()
    {
        if (!_buffering || !_started || WantsPause || _stopping || _loadId == 0)
        {
            StopStallWatch();
            return;
        }
        if (_stallWatch is not null && Math.Abs(_playerPosition - _stallAt) < 1 && _heldTo - _stallHeldTo <= StallGrowth)
        {
            // Nothing new; data counts from the least mpv held.
            _stallHeldTo = Math.Min(_stallHeldTo, _heldTo);
            return;
        }
        StopStallWatch();
        _stallAt = _playerPosition;
        _stallHeldTo = _heldTo;
        long loadId = _loadId;
        int watch = ++_stallWatches;
        _stallWatch = _time.CreateTimer(_ => _dispatcher.Post(() => OnStalled(loadId, watch)), null, StallTimeout, Timeout.InfiniteTimeSpan);
    }

    private void OnStalled(long loadId, int watch)
    {
        if (watch != _stallWatches || _stallWatch is null || loadId != _loadId || _stopping)
        {
            return;
        }
        StopStallWatch();
        if (WantsPause)
        {
            return;
        }
        if (_recovering)
        {
            // The recovery replaces the stream, or plays on with it: then the wait starts again.
            WatchStall();
            return;
        }
        _logger.LogWarning("The stream waited {Seconds:F0} s for data at {Position:F1} s; giving it up", StallTimeout.TotalSeconds, SourcePosition);
        // What mpv still waits for must not play behind the reload; the reconnect is a lost connection's.
        _engine.Stop();
        OnLoadEnded(new PlaybackEnded(PlaybackEndReason.Error, "No data within the stall timeout.", loadId));
    }

    // A pause mpv shows, or one asked that it has not shown yet.
    private bool WantsPause => _paused || _engine.IsPauseAsked;

    // A new load, the end of one, or the end of the playback: the wait is over without a verdict.
    private void ForgetBuffering()
    {
        _buffering = false;
        StopStallWatch();
    }

    private void StopStallWatch()
    {
        _stallWatch?.Dispose();
        _stallWatch = null;
    }

    // What mpv holds from the position on, in seconds; 0 when it holds nothing there.
    private static double HeldAhead(PlaybackSnapshot state)
    {
        double at = state.Position.TotalSeconds;
        return state.Buffered.Where(range => range.StartSeconds <= at + 1 && range.EndSeconds > at)
            .Select(range => range.EndSeconds - at)
            .FirstOrDefault();
    }
}
