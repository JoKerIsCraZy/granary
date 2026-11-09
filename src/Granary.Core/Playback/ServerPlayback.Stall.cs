using Microsoft.Extensions.Logging;

namespace Granary.Core.Playback;

// A stream that stops delivering while the viewer plays (docs/api-contract.md, 9.3). The media proxy cuts off a server
// gone silent, so mpv asks again by itself; a wait for data that still holds after that is given up and the stream
// reconnected, as Android gives a stalled load 90 s before it gives up.
public sealed partial class ServerPlayback
{
    // Above the server's longest hold of a request (a playlist 30 s, a segment 12 s and 30 s more after a seek, 9.5),
    // and above the proxy's minute without data (MediaRoute.UpstreamIdleTimeout), after which mpv's reader asks again on
    // its own and usually recovers in place. Android's retry window, and the time a load gets for its first picture.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(90);

    // Data still arrives while what mpv holds grows by more than this, in seconds of the title: a slow link (a remote
    // server, a high bitrate, a long cache-pause-wait) can wait longer than the limit and is not lost, as Apple's
    // detector counts bytes, not time. mpv reports what it holds at most four times a second, in steps of a frame.
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

    // A wait counts while the viewer wants the picture: not while they paused, nor before the stream's first frame (the
    // first-frame watch has that). A new position, as a seek during the wait, or data that arrived starts it again.
    private void WatchStall()
    {
        if (!_buffering || !_started || _paused || _stopping || _loadId == 0)
        {
            StopStallWatch();
            return;
        }
        if (_stallWatch is not null && Math.Abs(_playerPosition - _stallAt) < 1 && _heldTo - _stallHeldTo <= StallGrowth)
        {
            // Nothing new. Less held than before (mpv let go of some) is no data either, and what arrives counts from there.
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
        if (_recovering)
        {
            // The recovery replaces the stream, or plays on with it: then the wait starts again.
            WatchStall();
            return;
        }
        _logger.LogWarning("The stream waited {Seconds:F0} s for data at {Position:F1} s; giving it up", StallTimeout.TotalSeconds, SourcePosition);
        // Whatever mpv still waits for must not start playing behind the reload. The reconnect asks the session for the
        // stream again where the viewer is, as after any connection lost once the stream played.
        _engine.Stop();
        OnLoadEnded(new PlaybackEnded(PlaybackEndReason.Error, "No data within the stall timeout.", loadId));
    }

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
