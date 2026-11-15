using System.Net.Sockets;
using Granary.Core.Remote;
using Microsoft.Extensions.Logging;

namespace Granary.App.Services;

/// <summary>
/// The accept loop of <see cref="TcpRemoteListener"/>, apart from its socket so that tests run it without one: it takes
/// one connection after another and hands each over without waiting for what becomes of it, and stops at once, never
/// waiting for a pending accept, a connection it handed over, or the task that connection was handed to.
/// <para>
/// A connection that fails as it is accepted is skipped, whatever a peer does: a peer could make every accept fail, and
/// it must neither stop the loop nor make it spin, so failures that come back to back are waited out, a little longer each
/// time, up to <see cref="LongestBackoff"/>. Only a failure of the listener itself ends the loop: an error of its own, or
/// a shortage of sockets or buffers lasting <see cref="GiveUpAfter"/> from its own start, no connection getting through and
/// <see cref="ShortagesToGiveUp"/> accepts or more in that time failing for it (out of them for good). A peer's resets and
/// broken connections never end the loop, however long they go on, nor start, keep or hide such a shortage; a shortage
/// now and then is none.
/// The owner is told (<c>onFailed</c>), so no port stays open and advertised that nobody answers.
/// </para>
/// It logs by fixed phrases and an exception's type and code only (<see cref="RemoteLog"/>): a socket's message can name
/// an address.
/// </summary>
internal sealed class RemoteAcceptLoop
{
    /// <summary>The wait after the second failed connection in a row; it doubles with each one after.</summary>
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromMilliseconds(50);

    /// <summary>The longest wait between failed connections in a row.</summary>
    public static readonly TimeSpan LongestBackoff = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long accepts may fail for want of sockets or buffers, none getting through, before it counts as the listener's own
    /// failure: out of them for good, a port advertised that nobody answers.
    /// </summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many accepts must have failed for want of sockets or buffers within the last <see cref="GiveUpAfter"/>: a few
    /// among a peer's resets, or now and then over minutes, are no lasting shortage. A lasting one fails at least once a
    /// second (<see cref="LongestBackoff"/>), and every other second with a peer's resets among it: more than this.
    /// </summary>
    public const int ShortagesToGiveUp = 10;

    private const int NotStarted = 0;
    private const int Accepting = 1;
    private const int Stopped = 2;

    private readonly Func<CancellationToken, ValueTask<IRemoteConnection>> _accept;
    private readonly Func<IRemoteConnection, Task> _onAccepted;
    private readonly Action _onFailed;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    // Taken once: the source is disposed at the stop, and a cancelled token is read safely after that.
    private readonly CancellationToken _stopToken;
    private int _state = NotStarted;

    /// <param name="accept">Waits for the next connection; the token ends it when the loop stops, if it heeds it.</param>
    /// <param name="onAccepted">Takes a connection over; the task it answers runs as long as the connection and is never awaited.</param>
    /// <param name="onFailed">The listener itself failed and the loop ended; never called for a stop.</param>
    /// <param name="time">The clock the waits between failed connections run on.</param>
    /// <param name="logger">Fixed phrases only.</param>
    public RemoteAcceptLoop(Func<CancellationToken, ValueTask<IRemoteConnection>> accept, Func<IRemoteConnection, Task> onAccepted,
        Action onFailed, TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(accept);
        ArgumentNullException.ThrowIfNull(onAccepted);
        ArgumentNullException.ThrowIfNull(onFailed);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _accept = accept;
        _onAccepted = onAccepted;
        _onFailed = onFailed;
        _time = time;
        _logger = logger;
        _stopToken = _stop.Token;
    }

    /// <summary>Ends when the loop does. Nothing waits for it.</summary>
    public Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>Starts taking connections, once; after <see cref="Stop"/> it does nothing.</summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref _state, Accepting, NotStarted) == NotStarted)
        {
            var stop = _stopToken;
            Running = Task.Run(() => LoopAsync(stop), CancellationToken.None);
        }
    }

    /// <summary>
    /// Stops taking connections, at once: an accept that heeds the stop ends, one that does not is left behind, and a
    /// connection it brings after the stop is closed, not handed over. Safe twice, and before <see cref="Start"/>.
    /// </summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _state, Stopped) == Stopped)
        {
            return;
        }
        try
        {
            _stop.Cancel();
        }
        catch (AggregateException ex)
        {
            // What an accept registered on the stop failed; the stop holds all the same.
            _logger.LogDebug("Stopping the phone remote's accepts failed ({Error})", RemoteLog.Failure(ex.InnerException ?? ex));
        }
        finally
        {
            // Once, after the cancel: every registration has run, and whatever reads the token later finds it cancelled.
            _stop.Dispose();
        }
    }

    private bool IsStopped => Volatile.Read(ref _state) == Stopped;

    private async Task LoopAsync(CancellationToken stop)
    {
        int failures = 0;
        // When accepts failed for want of sockets or buffers within the last GiveUpAfter, and since when they have without a
        // gap that long: a lasting shortage fills that window from its own start, a few far apart never do, and a peer's
        // resets neither start nor keep its clock. Both start anew when a connection gets through.
        var shortages = new Queue<long>();
        long shortSince = 0;
        while (!IsStopped)
        {
            IRemoteConnection connection;
            try
            {
                connection = await _accept(stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsStopped && ex is not OutOfMemoryException)
            {
                // The listener closed under the accept: what a stop does.
                return;
            }
            catch (Exception ex) when (IsOneConnections(ex))
            {
                // Logged once for a run of them: a peer that makes every accept fail must not fill the log either.
                if (++failures == 1)
                {
                    _logger.LogDebug("A connection ended while it was accepted ({Error})", RemoteLog.Failure(ex));
                }
                // Only a shortage on this PC can last: a peer's resets and broken connections never end the loop, however
                // long they go on. Nothing got through for the half minute, and its accepts kept failing for want of
                // sockets or buffers: that does.
                long now = _time.GetTimestamp();
                // Half a minute old is out: a shortage that comes that long after the last starts a clock of its own.
                while (shortages.TryPeek(out long oldest) && _time.GetElapsedTime(oldest, now) >= GiveUpAfter)
                {
                    shortages.Dequeue();
                }
                if (IsShortage(ex))
                {
                    if (shortages.Count == 0)
                    {
                        shortSince = now;
                    }
                    shortages.Enqueue(now);
                }
                if (IsShortage(ex) && shortages.Count >= ShortagesToGiveUp
                    && _time.GetElapsedTime(shortSince, now) >= GiveUpAfter)
                {
                    _logger.LogWarning("The phone remote's listener stopped taking connections: {Count} failed in a row, none got through ({Error})",
                        failures, RemoteLog.Failure(ex));
                    Failed();
                    return;
                }
                if (failures > 1 && !await BackOffAsync(failures, stop).ConfigureAwait(false))
                {
                    return;
                }
                continue;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning("The phone remote's listener stopped taking connections ({Error})", RemoteLog.Failure(ex));
                Failed();
                return;
            }
            if (failures > 1)
            {
                _logger.LogDebug("{Count} connections in a row ended while they were accepted", failures);
            }
            failures = 0;
            shortages.Clear();
            if (IsStopped)
            {
                await CloseAsync(connection).ConfigureAwait(false);
                return;
            }
            HandOver(connection);
        }
    }

    // Waits before the next accept; false when the loop was stopped meanwhile.
    private async Task<bool> BackOffAsync(int failures, CancellationToken stop)
    {
        long ticks = FirstBackoff.Ticks << Math.Min(failures - 2, 10);
        try
        {
            await Task.Delay(TimeSpan.FromTicks(Math.Min(ticks, LongestBackoff.Ticks)), _time, stop).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void Failed()
    {
        try
        {
            _onFailed();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError("The phone remote could not be told its listener failed ({Error})", RemoteLog.Failure(ex));
        }
    }

    // Hands the connection over without waiting for it; a failure of its task is the owner's, but never goes unseen.
    private void HandOver(IRemoteConnection connection)
    {
        Task serving;
        try
        {
            serving = _onAccepted(connection);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError("A phone's connection could not be taken over ({Error})", RemoteLog.Failure(ex));
            _ = CloseAsync(connection);
            return;
        }
        _ = serving.ContinueWith(static (task, state) =>
            ((ILogger)state!).LogError("A phone's connection failed ({Error})", RemoteLog.Failure(task.Exception!.GetBaseException())),
            _logger, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task CloseAsync(IRemoteConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _logger.LogDebug("A connection did not close cleanly ({Error})", RemoteLog.Failure(ex));
        }
    }

    // A connection that went away between its arrival and its accept, or a passing shortage: the listener itself is fine.
    // TcpRemoteListener reports what fails after the accept as an IOException.
    // Out of sockets or buffers: the accept failed for want of this PC's resources, not the peer's doing.
    private static bool IsShortage(Exception ex) =>
        ex is SocketException { SocketErrorCode: SocketError.NoBufferSpaceAvailable or SocketError.TooManyOpenSockets };

    private static bool IsOneConnections(Exception ex) => ex switch
    {
        SocketException socket => socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted
            or SocketError.NetworkReset or SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable
            or SocketError.HostDown or SocketError.NoBufferSpaceAvailable or SocketError.TooManyOpenSockets
            or SocketError.Interrupted or SocketError.TryAgain or SocketError.WouldBlock,
        IOException => true,
        _ => false,
    };
}
