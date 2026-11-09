using System.Globalization;

namespace Granary.Core.Playback;

/// <summary>
/// Times one media request's waits for the server: for the answer, then for each piece of its body, each between
/// <see cref="Arm"/> and <see cref="Disarm"/>. Time the request spends waiting for the player does not count: a paused
/// player stops reading for as long as it likes. A wait that lasts the limit cancels <see cref="Token"/> and counts as
/// <see cref="IsCutOff"/>.
/// </summary>
internal sealed class UpstreamWatch : IDisposable
{
    private readonly TimeSpan _limit;
    private readonly TimeProvider _time;
    private readonly CancellationToken _closing;
    private readonly CancellationTokenSource _waits;
    private readonly ITimer _timer;
    private readonly Lock _gate = new();

    // When the wait that is timed began; null between waits.
    private long? _armedAt;
    private bool _cut;

    /// <param name="closing">Ends the waits without a failure of the server's: the route closed, or the player left.</param>
    public UpstreamWatch(TimeSpan limit, TimeProvider time, CancellationToken closing)
    {
        _limit = limit;
        _time = time;
        _closing = closing;
        _waits = CancellationTokenSource.CreateLinkedTokenSource(closing);
        _timer = time.CreateTimer(static state => ((UpstreamWatch)state!).Expire(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>For the waits on the server: cancelled when the route closes or a wait lasted the limit.</summary>
    public CancellationToken Token => _waits.Token;

    /// <summary>A wait lasted the limit: the server went silent. A route that closed is not that.</summary>
    public bool IsCutOff
    {
        get
        {
            lock (_gate)
            {
                return _cut && !_closing.IsCancellationRequested;
            }
        }
    }

    /// <summary>A wait for the server begins.</summary>
    public void Arm()
    {
        lock (_gate)
        {
            _armedAt = _time.GetTimestamp();
        }
        _timer.Change(_limit, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The wait ended, however it did: the time until the next one does not count.</summary>
    public void Disarm()
    {
        lock (_gate)
        {
            _armedAt = null;
        }
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The failure of a wait that <see cref="IsCutOff"/> ended; it names neither the URL nor a header.</summary>
    public MediaUpstreamException CutOff(Exception inner) =>
        new(string.Create(CultureInfo.InvariantCulture, $"sent nothing for {_limit.TotalSeconds:F0} s"), inner);

    public void Dispose()
    {
        _timer.Dispose();
        _waits.Dispose();
    }

    // On the timer's thread, where a callback can run late: after the wait it was for ended, or once a younger one began.
    // Only a wait that is still timed and has lasted the limit is cut. The wait's own cancellation (closing the
    // connection) runs off this thread.
    private void Expire()
    {
        lock (_gate)
        {
            if (_cut || _armedAt is not { } armed || _time.GetElapsedTime(armed) < _limit)
            {
                return;
            }
            _cut = true;
            try
            {
                _ = _waits.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // The request ended meanwhile.
            }
        }
    }
}

/// <summary>
/// The server's side of a media request failed after it was sent: it went silent or broke off. The message says which,
/// without the URL or any header.
/// </summary>
internal sealed class MediaUpstreamException(string failure, Exception? inner = null) : IOException(failure, inner);
