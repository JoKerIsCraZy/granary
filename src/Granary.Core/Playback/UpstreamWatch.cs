using System.Globalization;

namespace Granary.Core.Playback;

/// <summary>
/// Times one media request's waits for the server: for the answer, then for each piece of its body. Time the request
/// spends waiting for the player does not count: a paused player stops reading for as long as it likes. A wait longer
/// than the limit is cut off with a <see cref="MediaUpstreamException"/>.
/// </summary>
internal sealed class UpstreamWatch : IDisposable
{
    private readonly TimeSpan _limit;
    private readonly CancellationToken _closing;
    private readonly CancellationTokenSource _silent = new();
    private readonly CancellationTokenSource _waits;
    private readonly ITimer _timer;

    /// <param name="closing">Ends the waits without a failure of the server's: the route closed, or the player left.</param>
    public UpstreamWatch(TimeSpan limit, TimeProvider time, CancellationToken closing)
    {
        _limit = limit;
        _closing = closing;
        _waits = CancellationTokenSource.CreateLinkedTokenSource(closing, _silent.Token);
        _timer = time.CreateTimer(static state => ((UpstreamWatch)state!).Expire(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Waits for the server through <paramref name="wait"/>, which is cancelled when the server keeps silent too long.</summary>
    public async ValueTask<T> WaitAsync<T>(Func<CancellationToken, ValueTask<T>> wait)
    {
        ArgumentNullException.ThrowIfNull(wait);
        _timer.Change(_limit, Timeout.InfiniteTimeSpan);
        try
        {
            return await wait(_waits.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (_silent.IsCancellationRequested && !_closing.IsCancellationRequested)
        {
            throw new MediaUpstreamException(string.Create(CultureInfo.InvariantCulture, $"sent nothing for {_limit.TotalSeconds:F0} s"), ex);
        }
        finally
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _waits.Dispose();
        _silent.Dispose();
    }

    // On the timer's thread: the wait's own cancellation (closing the connection) runs off it.
    private void Expire()
    {
        try
        {
            _ = _silent.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // The request ended meanwhile.
        }
    }
}

/// <summary>
/// The server's side of a media request failed after it was sent: it went silent or broke off. The message says which,
/// without the URL or any header.
/// </summary>
internal sealed class MediaUpstreamException(string failure, Exception? inner = null) : IOException(failure, inner);
