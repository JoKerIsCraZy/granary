namespace Granary.Core.Remote;

// The heartbeat, as silo-roku's receiver.bs ticks it: a ping every PingEvery, and more than MaxMissedPongs in a row
// unanswered drops the phone. A pong counts only once a ping has left for the phone, so a phone that reads nothing cannot
// keep itself connected by sending pongs alone.
public sealed partial class RemoteController
{
    private ITimer? _heartbeat;
    private int _missedPongs;
    private int _pingsOut;

    private void StartHeartbeat()
    {
        var heartbeat = _time.CreateTimer(static self => ((RemoteController)self!).Beat(), this, _timings.PingEvery, _timings.PingEvery);
        Volatile.Write(ref _heartbeat, heartbeat);
        if (!IsOpen)
        {
            // Ended while it was made.
            heartbeat.Dispose();
        }
    }

    private void StopHeartbeat() => Volatile.Read(ref _heartbeat)?.Dispose();

    private void Beat()
    {
        if (!IsOpen)
        {
            return;
        }
        if (Interlocked.Increment(ref _missedPongs) > _timings.MaxMissedPongs)
        {
            Drop(RemoteLogReasons.NoPongs);
            return;
        }
        _ = PingAsync();
    }

    private async Task PingAsync()
    {
        bool left = false;
        try
        {
            if (await EnterWriteGateAsync().ConfigureAwait(false))
            {
                try
                {
                    left = IsOpen && await WritePendingStateLockedAsync().ConfigureAwait(false)
                        && await WriteSealedLockedAsync(SiloCastMessages.Ping()).ConfigureAwait(false);
                    if (left)
                    {
                        // Counted once it left, as silo-roku's pingSent counts it: a ping still stuck in a full pipe
                        // makes no pong count.
                        Interlocked.Increment(ref _pingsOut);
                    }
                }
                finally
                {
                    ReleaseWriteGate();
                }
            }
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            Drop(RemoteLogReasons.PhoneLeft);
        }
    }

    private void OnPong()
    {
        if (Interlocked.Exchange(ref _pingsOut, 0) > 0)
        {
            Volatile.Write(ref _missedPongs, 0);
        }
    }
}
