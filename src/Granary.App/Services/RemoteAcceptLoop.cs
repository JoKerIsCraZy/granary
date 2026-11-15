using System.Net.Sockets;
using Granary.Core.Remote;
using Microsoft.Extensions.Logging;

namespace Granary.App.Services;

/// <summary>
/// The accept loop of <see cref="TcpRemoteListener"/>, apart from its socket so that tests run it without one: it takes
/// one connection after another and hands each over without waiting for what becomes of it, goes on past a connection
/// that fails while it is accepted, and stops at once, never waiting for a pending accept, a connection it handed over, or
/// the task that connection was handed to. It logs by fixed phrases and an exception's type and code only
/// (<see cref="RemoteLog"/>): a socket's message can name an address.
/// </summary>
internal sealed class RemoteAcceptLoop
{
    /// <summary>Failures in a row after which the loop gives up rather than spin: the listener itself is broken then.</summary>
    public const int MaxFailuresInARow = 10;

    private const int NotStarted = 0;
    private const int Accepting = 1;
    private const int Stopped = 2;

    private readonly Func<CancellationToken, ValueTask<IRemoteConnection>> _accept;
    private readonly Func<IRemoteConnection, Task> _onAccepted;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private int _state = NotStarted;

    /// <param name="accept">Waits for the next connection; the token ends it when the loop stops, if it heeds it.</param>
    /// <param name="onAccepted">Takes a connection over; the task it answers runs as long as the connection and is never awaited.</param>
    /// <param name="logger">Fixed phrases only.</param>
    public RemoteAcceptLoop(Func<CancellationToken, ValueTask<IRemoteConnection>> accept, Func<IRemoteConnection, Task> onAccepted, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(accept);
        ArgumentNullException.ThrowIfNull(onAccepted);
        ArgumentNullException.ThrowIfNull(logger);
        _accept = accept;
        _onAccepted = onAccepted;
        _logger = logger;
    }

    /// <summary>Ends when the loop does. Nothing waits for it.</summary>
    public Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>Starts taking connections, once; after <see cref="Stop"/> it does nothing.</summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref _state, Accepting, NotStarted) == NotStarted)
        {
            var stop = _stop.Token;
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
    }

    private bool IsStopped => Volatile.Read(ref _state) == Stopped;

    private async Task LoopAsync(CancellationToken stop)
    {
        int failures = 0;
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
                if (++failures >= MaxFailuresInARow)
                {
                    _logger.LogWarning("The phone remote's listener stopped taking connections after {Count} failed in a row ({Error})",
                        failures, RemoteLog.Failure(ex));
                    return;
                }
                _logger.LogDebug("A connection ended while it was accepted ({Error})", RemoteLog.Failure(ex));
                continue;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning("The phone remote's listener stopped taking connections ({Error})", RemoteLog.Failure(ex));
                return;
            }
            failures = 0;
            if (IsStopped)
            {
                await CloseAsync(connection).ConfigureAwait(false);
                return;
            }
            HandOver(connection);
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

    // A connection that went away between its arrival and its accept: the listener itself is fine.
    private static bool IsOneConnections(Exception ex) => ex switch
    {
        SocketException socket => socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted
            or SocketError.NetworkReset or SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable,
        IOException => true,
        _ => false,
    };
}
