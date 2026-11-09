using System.Globalization;

namespace Granary.Core.Playback;

/// <summary>
/// Times one media request's waits for the server: for the answer, then for each piece of its body, each between
/// <see cref="Arm"/> and <see cref="Disarm"/>. Time the request spends waiting for the player does not count: a paused
/// player stops reading for as long as it likes. A wait that lasts the limit is cut: its token is cancelled and the watch
/// reads <see cref="IsCutOff"/>.
/// </summary>
internal sealed class UpstreamWatch : IDisposable
{
    private readonly TimeSpan _limit;
    private readonly TimeProvider _time;
    private readonly CancellationToken _closing;
    private readonly ITimer _timer;
    private readonly Lock _gate = new();

    // The latest wait's own token source, linked to the closing: a cut ends that wait alone, also one whose bytes came in
    // the same moment, and the next wait starts clean. Null before the first wait.
    private CancellationTokenSource? _wait;

    // When the timed wait began; null between waits.
    private long? _armedAt;

    // The latest wait was cut.
    private bool _cut;

    /// <param name="closing">Ends the waits without a failure of the server's: the route closed, or the player left.</param>
    public UpstreamWatch(TimeSpan limit, TimeProvider time, CancellationToken closing)
    {
        _limit = limit;
        _time = time;
        _closing = closing;
        _timer = time.CreateTimer(static state => ((UpstreamWatch)state!).Expire(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The latest wait lasted the limit: the server went silent. A route that closed is not that.</summary>
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

    /// <summary>A wait for the server begins; its token is cancelled when the route closes or the wait lasts the limit.</summary>
    public CancellationToken Arm()
    {
        lock (_gate)
        {
            Retire();
            _wait = CancellationTokenSource.CreateLinkedTokenSource(_closing);
            _cut = false;
            _armedAt = _time.GetTimestamp();
            _timer.Change(_limit, Timeout.InfiniteTimeSpan);
            return _wait.Token;
        }
    }

    /// <summary>The wait ended, however it did: the time until the next one does not count.</summary>
    public void Disarm()
    {
        lock (_gate)
        {
            _armedAt = null;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The failure of a wait that <see cref="IsCutOff"/> ended; it names neither the URL nor a header.</summary>
    public MediaUpstreamException CutOff(Exception inner) =>
        new(string.Create(CultureInfo.InvariantCulture, $"sent nothing for {_limit.TotalSeconds:F0} s"), inner);

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate)
        {
            Retire();
            _wait = null;
            _armedAt = null;
        }
    }

    // On the timer's thread, where a callback can come late (after its wait ended, or once a younger one began) or a
    // little early (the timer counts in coarser ticks than GetTimestamp). Only the wait that is timed is looked at: cut
    // once it has lasted the limit, and otherwise looked at again when the rest has passed.
    private void Expire()
    {
        lock (_gate)
        {
            if (_cut || _armedAt is not { } armed || _wait is not { } wait)
            {
                return;
            }
            var elapsed = _time.GetElapsedTime(armed);
            if (elapsed < _limit)
            {
                _timer.Change(_limit - elapsed, Timeout.InfiniteTimeSpan);
                return;
            }
            _cut = true;
            // Off this thread: the wait's cancellation closes its connection. The source goes once that has run.
            _ = wait.CancelAsync().ContinueWith(static (_, source) => ((CancellationTokenSource)source!).Dispose(), wait,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    // Under _gate: the latest wait's source is done with. One a cut cancels is disposed by that cut once it has run.
    private void Retire()
    {
        if (_wait is { } wait && !_cut)
        {
            wait.Dispose();
        }
    }
}

/// <summary>
/// The server's side of a media request failed after it was sent: it went silent or broke off. The message says which,
/// without the URL or any header.
/// </summary>
internal sealed class MediaUpstreamException(string failure, Exception? inner = null) : IOException(failure, inner);
