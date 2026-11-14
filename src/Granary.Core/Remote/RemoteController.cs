using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Remote;

/// <summary>
/// How long a phone's connection may take for each step, and how much may wait for it, as Silo's Roku app sets them
/// (silo-roku's <c>receiver.bs</c> and <c>ControlTask.bs</c>).
/// </summary>
/// <param name="HelloWait">For the <c>sealed_hello</c> after connecting, and again for the <c>hello</c> after it.</param>
/// <param name="PingEvery">How often the phone is pinged.</param>
/// <param name="MaxMissedPongs">Pings in a row it may leave unanswered; one more drops it.</param>
/// <param name="CloseGrace">How long a <c>close</c> may take to leave before the connection is closed anyway.</param>
/// <param name="WriteStall">How long one write may wait for a phone that reads nothing.</param>
/// <param name="MaxQueuedBytes">How much may wait to be sent to one phone.</param>
public sealed record RemoteTimings(
    TimeSpan HelloWait,
    TimeSpan PingEvery,
    int MaxMissedPongs,
    TimeSpan CloseGrace,
    TimeSpan WriteStall,
    int MaxQueuedBytes)
{
    public static RemoteTimings Default { get; } = new(
        HelloWait: TimeSpan.FromSeconds(5),
        PingEvery: TimeSpan.FromSeconds(3),
        MaxMissedPongs: 3,
        CloseGrace: TimeSpan.FromMilliseconds(500),
        WriteStall: TimeSpan.FromSeconds(10),
        MaxQueuedBytes: 128 * 1024);
}

/// <summary>
/// One phone's connection, from its <c>sealed_hello</c> to its end, in the order of events of silo-roku's
/// <c>ControlTask.bs</c>: the keyed handshake, every later frame sealed and opened in sequence, the heartbeat, and the
/// deadlines. The receiver decides what a message means; this type only carries it.
/// <para>
/// Every write goes through one gate, inside which the next sequence number is taken, the frame sealed and written: the
/// phone needs the numbers in the order the frames arrive. Nothing but one small <c>sealed_hello</c> is read from a peer
/// that has not shown the key, and nothing is sent after it but sealed frames and a plain refusal. A frame that does not
/// open, or arrives out of sequence, ends the connection without an answer. A connection ends by closing its stream,
/// never by cancelling a read.
/// </para>
/// </summary>
public sealed partial class RemoteController : IAsyncDisposable
{
    private const int Open = 0;
    private const int Closing = 1;
    private const int Ending = 2;

    private readonly IRemoteConnection _connection;
    private readonly Stream _stream;
    private readonly Func<PairingMaterial?> _pairing;
    private readonly RemoteTimings _timings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SealedSessionKeys? _keys;
    private string? _keyId;
    private ITimer? _helloWatch;
    private int _state;
    private int _ending;
    private long _queuedBytes;
    private Func<byte[]?>? _pendingState;

    // Taken and advanced inside the write gate only.
    private ulong _sendSeq;

    // Read and advanced by the read loop only, after a frame opened.
    private ulong _receiveSeq;

    /// <param name="number">The connection's number in the log.</param>
    /// <param name="connection">The accepted connection, which this controller owns from now on.</param>
    /// <param name="pairing">
    /// The pairing material as it is at each moment: read at the handshake and checked again with every frame, so a
    /// phone on a code that was reset is refused even if the news of the reset never came.
    /// </param>
    /// <param name="timings">The deadlines and limits.</param>
    /// <param name="time">The clock every deadline runs on.</param>
    /// <param name="logger">Where the connection's opening and end are logged, by number and reason only.</param>
    public RemoteController(int number, IRemoteConnection connection, Func<PairingMaterial?> pairing, RemoteTimings timings,
        TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(timings);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        Number = number;
        _connection = connection;
        _stream = connection.Stream;
        _pairing = pairing;
        _timings = timings;
        _time = time;
        _logger = logger;
        ConnectedAt = time.GetTimestamp();
    }

    /// <summary>The connection's number in the log.</summary>
    public int Number { get; }

    /// <summary>When the connection was accepted, as a <see cref="TimeProvider"/> timestamp.</summary>
    public long ConnectedAt { get; }

    /// <summary>The phone's own id from its <c>hello</c>; set by the receiver.</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>The phone's name from its <c>hello</c>, as it sent it; set by the receiver.</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>The features the phone lists; set by the receiver. A <c>close</c> carries its reason only with <c>close_reason</c>.</summary>
    public IReadOnlySet<string> Features { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The phone said <c>hello</c>; set by the receiver. Until then the connection closes after <see cref="RemoteTimings.HelloWait"/>.</summary>
    public bool SaidHello { get; set; }

    /// <summary>The phone is on Granary's server and gets the status; set by the receiver.</summary>
    public bool IsAuthorized { get; set; }

    /// <summary>The phone only watches and is not actively controlling; set by the receiver.</summary>
    public bool IsPassive { get; set; }

    /// <summary>The connection takes messages: it is neither closing nor ended.</summary>
    public bool IsOpen => Volatile.Read(ref _state) == Open;

    /// <summary>The handshake is done: frames go sealed.</summary>
    public bool IsKeyed => Volatile.Read(ref _keys) is not null;

    /// <summary>Completes once the connection is closed and its keys are gone.</summary>
    public Task Ended => _ended.Task;

    /// <summary>The session's keys are still held: false once the connection ended.</summary>
    internal bool HoldsSessionKeys => Volatile.Read(ref _keys) is { IsDisposed: false };

    /// <summary>
    /// The <c>sealed_hello</c> exchange: one frame of at most <see cref="SiloCastFrame.MaxFirstFrameBytes"/> within
    /// <see cref="RemoteTimings.HelloWait"/>. A <c>sealed_hello</c> under the current key's id is answered with the
    /// receiver's own and a fresh nonce, which keys the session; anything else gets the plain <c>close</c> with reason
    /// <c>unauthorized</c>. False once the connection was refused or ended.
    /// </summary>
    public async Task<bool> OpenAsync(CancellationToken cancellationToken)
    {
        using var stop = cancellationToken.Register(static self => ((RemoteController)self!).Drop(RemoteLogReasons.Stopped), this);
        byte[]? first;
        using (_time.CreateTimer(static self => ((RemoteController)self!).Drop(RemoteLogReasons.NoPairingHello), this,
            _timings.HelloWait, Timeout.InfiniteTimeSpan))
        {
            try
            {
                first = await SiloCastFrame.ReadAsync(_stream, SiloCastFrame.MaxFirstFrameBytes, CancellationToken.None).ConfigureAwait(false);
            }
            catch (FrameTooLargeException)
            {
                await EndAsync(RemoteLogReasons.FirstFrameTooLarge).ConfigureAwait(false);
                return false;
            }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                await EndAsync(RemoteLogReasons.PhoneLeft).ConfigureAwait(false);
                return false;
            }
        }
        if (first is null)
        {
            await EndAsync(RemoteLogReasons.PhoneLeft).ConfigureAwait(false);
            return false;
        }
        if (!IsOpen)
        {
            await Ended.ConfigureAwait(false);
            return false;
        }

        // The material as it is now: a code reset a moment ago already counts.
        var pairing = _pairing();
        var hello = SiloCastSealed.ReadHello(first);
        if (pairing is null || hello is null || !string.Equals(hello.KeyId, pairing.KeyId, StringComparison.Ordinal))
        {
            await RefuseAsync(hello is null ? RemoteLogReasons.NotAPairingHello : RemoteLogReasons.WrongKey).ConfigureAwait(false);
            return false;
        }
        return await KeyAsync(pairing, hello).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the phone's frames until the connection ends, each opened as the next in sequence. <c>ping</c>, <c>pong</c>
    /// and the phone's <c>close</c> are handled here; every other message goes to <paramref name="onMessage"/>, one at a
    /// time. A message that cannot be read ends the connection with a bare <c>close</c>.
    /// </summary>
    public async Task RunAsync(Func<SiloCastMessage, ValueTask> onMessage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        if (Volatile.Read(ref _keys) is not { } keys)
        {
            throw new InvalidOperationException("The connection is not keyed.");
        }
        using var stop = cancellationToken.Register(static self => ((RemoteController)self!).Drop(RemoteLogReasons.Stopped), this);
        while (await ReadMessageAsync(keys).ConfigureAwait(false) is { } message)
        {
            if (!IsOpen)
            {
                // A closing connection reads nothing more (silo-roku's ControlTask.bs receive).
                continue;
            }
            if (!await HandleAsync(message, onMessage).ConfigureAwait(false))
            {
                break;
            }
        }
        await Ended.ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one message sealed, after the status waiting for this phone if there is one. Does nothing before the session
    /// is keyed or once the connection closes. More than <see cref="RemoteTimings.MaxQueuedBytes"/> waiting drops the phone.
    /// </summary>
    public async ValueTask SendAsync(byte[] message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsOpen || !IsKeyed)
        {
            return;
        }
        if (Interlocked.Add(ref _queuedBytes, message.Length) > _timings.MaxQueuedBytes)
        {
            Interlocked.Add(ref _queuedBytes, -message.Length);
            Drop(RemoteLogReasons.NotReading);
            return;
        }
        try
        {
            if (!await EnterWriteGateAsync().ConfigureAwait(false))
            {
                return;
            }
            try
            {
                if (IsOpen && await WritePendingStateLockedAsync().ConfigureAwait(false))
                {
                    await WriteSealedLockedAsync(message).ConfigureAwait(false);
                }
            }
            finally
            {
                ReleaseWriteGate();
            }
        }
        finally
        {
            Interlocked.Add(ref _queuedBytes, -message.Length);
        }
    }

    /// <summary>
    /// Offers the status: it is made by <paramref name="newest"/> only when its turn to be sealed comes, and a newer offer
    /// replaces one still waiting, so a phone that reads slowly gets the newest status rather than every one. Null from
    /// <paramref name="newest"/> sends nothing.
    /// </summary>
    public void OfferState(Func<byte[]?> newest)
    {
        ArgumentNullException.ThrowIfNull(newest);
        if (!IsOpen || !IsKeyed)
        {
            return;
        }
        Volatile.Write(ref _pendingState, newest);
        if (_writeGate.Wait(0))
        {
            _ = Task.Run(PumpStateAsync);
        }
    }

    /// <summary>
    /// Closes the connection with a <c>close</c>, its reason only for a phone that lists <c>close_reason</c>, and waits at
    /// most <see cref="RemoteTimings.CloseGrace"/> for it to leave. <paramref name="logReason"/> is one of
    /// <see cref="RemoteLogReasons"/> or another fixed phrase, never a value from a frame.
    /// </summary>
    public Task CloseAsync(string? reason, string logReason) => CloseAsync(reason, logReason, evenUnlisted: false);

    /// <summary>
    /// As <see cref="CloseAsync(string?, string)"/>; with <paramref name="evenUnlisted"/> the reason goes also to a phone
    /// that has not listed <c>close_reason</c>, as silo-roku's refusal of a controller too many does: every phone ignores
    /// what it does not read.
    /// </summary>
    public async Task CloseAsync(string? reason, string logReason, bool evenUnlisted)
    {
        ArgumentException.ThrowIfNullOrEmpty(logReason);
        if (Interlocked.CompareExchange(ref _state, Closing, Open) != Open)
        {
            await Ended.ConfigureAwait(false);
            return;
        }
        Volatile.Write(ref _pendingState, null);
        if (IsKeyed)
        {
            string? wire = reason is not null && (evenUnlisted || Features.Contains(SiloCastFeatures.CloseReason)) ? reason : null;
            var leaving = SendCloseAsync(SiloCastMessages.Close(wire));
            try
            {
                await leaving.WaitAsync(_timings.CloseGrace, _time).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A phone that is gone or reads nothing: closed without waiting longer.
            }
        }
        await EndAsync(logReason).ConfigureAwait(false);
    }

    /// <summary>Ends the connection at once, without a frame. <paramref name="logReason"/> as for <see cref="CloseAsync(string?, string)"/>.</summary>
    public void Drop(string logReason)
    {
        if (Volatile.Read(ref _ending) == 0)
        {
            _ = EndAsync(logReason);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Drop(RemoteLogReasons.Stopped);
        await Ended.ConfigureAwait(false);
        _lifetime.Dispose();
    }

    // Answers a sealed_hello under the key: the receiver's own, with a fresh nonce, so a recorded session cannot be played
    // back to it. The session is keyed only once the answer left; nothing sealed goes before it.
    private async Task<bool> KeyAsync(PairingMaterial pairing, SealedHello hello)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(SiloCastSealed.NonceBytes);
        var keys = SiloCastSealed.DeriveSessionKeys(pairing.Key, hello.Nonce, nonce);
        if (!await EnterWriteGateAsync().ConfigureAwait(false))
        {
            keys.Dispose();
            return false;
        }
        try
        {
            byte[] answer = Encoding.UTF8.GetBytes(SiloCastSealed.HelloText(pairing.KeyId, nonce));
            if (!IsOpen || !await WriteFrameLockedAsync(SiloCastFrame.Encode(answer)).ConfigureAwait(false))
            {
                keys.Dispose();
                return false;
            }
            _keyId = pairing.KeyId;
            Volatile.Write(ref _keys, keys);
        }
        finally
        {
            _writeGate.Release();
        }
        if (!IsOpen)
        {
            // Ended while the answer left: its end may have missed the keys.
            keys.Dispose();
            return false;
        }
        _helloWatch = _time.CreateTimer(static self => ((RemoteController)self!).HelloOverdue(), this, _timings.HelloWait, Timeout.InfiniteTimeSpan);
        StartHeartbeat();
        _logger.LogInformation("Connection {Number} opened", Number);
        return true;
    }

    // The plain close with reason unauthorized, as silo-roku's sealedHello answers a peer without the key, then the end.
    private async Task RefuseAsync(string logReason)
    {
        if (Interlocked.CompareExchange(ref _state, Closing, Open) != Open)
        {
            await Ended.ConfigureAwait(false);
            return;
        }
        var leaving = SendPlainAsync(SiloCastMessages.Close(SiloCastCloseReasons.Unauthorized));
        try
        {
            await leaving.WaitAsync(_timings.CloseGrace, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Not waited for longer.
        }
        await EndAsync(logReason).ConfigureAwait(false);
    }

    private void HelloOverdue()
    {
        if (!SaidHello && IsOpen)
        {
            _ = CloseAsync(SiloCastCloseReasons.Timeout, RemoteLogReasons.NoHello);
        }
    }

    // The next message, or null once the connection ended. A frame that does not open, or that comes while the code it
    // was keyed under is no longer the PC's, ends the connection unanswered.
    private async Task<SiloCastMessage?> ReadMessageAsync(SealedSessionKeys keys)
    {
        while (true)
        {
            byte[]? body;
            try
            {
                body = await SiloCastFrame.ReadAsync(_stream, SiloCastFrame.MaxFrameBytes, CancellationToken.None).ConfigureAwait(false);
            }
            catch (FrameTooLargeException)
            {
                Drop(RemoteLogReasons.FrameTooLarge);
                return null;
            }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                Drop(RemoteLogReasons.PhoneLeft);
                return null;
            }
            if (body is null)
            {
                Drop(RemoteLogReasons.PhoneLeft);
                return null;
            }
            if (!StillPaired())
            {
                return null;
            }

            byte[]? text;
            try
            {
                text = SiloCastSealed.Open(keys, SiloCastSealed.FromPhone, _receiveSeq, body);
            }
            catch (ObjectDisposedException)
            {
                // Ended under the read.
                return null;
            }
            if (text is null)
            {
                Drop(RemoteLogReasons.FrameDidNotOpen);
                return null;
            }
            _receiveSeq++;
            if (SiloCastMessages.Decode(text) is { } message)
            {
                return message;
            }
            if (IsOpen)
            {
                await CloseAsync(null, RemoteLogReasons.Malformed).ConfigureAwait(false);
                return null;
            }
        }
    }

    // False once the connection should stop reading.
    private async ValueTask<bool> HandleAsync(SiloCastMessage message, Func<SiloCastMessage, ValueTask> onMessage)
    {
        switch (message)
        {
            case SiloCastMessage.Ping:
                await SendAsync(SiloCastMessages.Pong()).ConfigureAwait(false);
                return true;
            case SiloCastMessage.Pong:
                OnPong();
                return true;
            case SiloCastMessage.Close:
                Drop(RemoteLogReasons.PhoneClosed);
                return false;
            case SiloCastMessage.Unknown:
                // A newer phone's type, or a control this build does not know: skipped.
                return true;
        }
        try
        {
            await onMessage(message).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError("Connection {Number}: a message failed ({Error})", Number, RemoteLog.Failure(ex));
            await CloseAsync(null, RemoteLogReasons.MessageFailed).ConfigureAwait(false);
            return false;
        }
    }

    // The key this session was keyed under is still the PC's. Checked with every frame in and out, so a reset takes a phone
    // off at its next frame even if the news of it never reached the receiver.
    private bool StillPaired()
    {
        if (_keyId is null || string.Equals(_pairing()?.KeyId, _keyId, StringComparison.Ordinal))
        {
            return true;
        }
        Drop(RemoteLogReasons.KeyReset);
        return false;
    }

    private async Task PumpStateAsync()
    {
        try
        {
            if (IsOpen)
            {
                await WritePendingStateLockedAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            ReleaseWriteGate();
        }
    }

    // Releases the gate, and takes it again for a status offered while it was held and not written by its holder.
    private void ReleaseWriteGate()
    {
        _writeGate.Release();
        if (Volatile.Read(ref _pendingState) is not null && IsOpen && _writeGate.Wait(0))
        {
            _ = Task.Run(PumpStateAsync);
        }
    }

    private async Task<bool> EnterWriteGateAsync()
    {
        try
        {
            await _writeGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    // Under the gate: the waiting status, made now. False when the connection failed.
    private async Task<bool> WritePendingStateLockedAsync()
    {
        if (Interlocked.Exchange(ref _pendingState, null) is not { } make)
        {
            return true;
        }
        byte[]? state;
        try
        {
            state = make();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError("Connection {Number}: the status could not be made ({Error})", Number, RemoteLog.Failure(ex));
            return true;
        }
        return state is null || await WriteSealedLockedAsync(state).ConfigureAwait(false);
    }

    // Under the gate: the message sealed under the next number. A message too large for a frame is not sent and takes no
    // number. False when the connection ended or failed.
    private async Task<bool> WriteSealedLockedAsync(byte[] message)
    {
        if (Volatile.Read(ref _keys) is not { } keys || Volatile.Read(ref _state) == Ending || !StillPaired())
        {
            return false;
        }
        byte[] frame;
        try
        {
            frame = SiloCastFrame.Encode(SiloCastSealed.Seal(keys, SiloCastSealed.FromReceiver, _sendSeq, message));
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (FrameTooLargeException)
        {
            _logger.LogWarning("Connection {Number}: a message too large for a frame was not sent", Number);
            return true;
        }
        _sendSeq++;
        return await WriteFrameLockedAsync(frame).ConfigureAwait(false);
    }

    private async Task<bool> SendCloseAsync(byte[] close)
    {
        if (!await EnterWriteGateAsync().ConfigureAwait(false))
        {
            return false;
        }
        try
        {
            return await WriteSealedLockedAsync(close).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<bool> SendPlainAsync(byte[] text)
    {
        if (!await EnterWriteGateAsync().ConfigureAwait(false))
        {
            return false;
        }
        try
        {
            return await WriteFrameLockedAsync(SiloCastFrame.Encode(text)).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // Under the gate: one frame, given at most WriteStall to leave. A phone that takes nothing for that long is dropped,
    // which closes the stream under the write. False when the connection failed.
    private async Task<bool> WriteFrameLockedAsync(byte[] frame)
    {
        using var stall = _time.CreateTimer(static self => ((RemoteController)self!).Drop(RemoteLogReasons.NotReading), this,
            _timings.WriteStall, Timeout.InfiniteTimeSpan);
        try
        {
            await _stream.WriteAsync(frame, CancellationToken.None).ConfigureAwait(false);
            await _stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            Drop(RemoteLogReasons.PhoneLeft);
            return false;
        }
    }

    // Once: closes the stream, which ends a read or write in progress, then forgets the session's keys. A seal or open
    // still under way keeps them whole until it is done (SealedSessionKeys).
    private async Task EndAsync(string logReason)
    {
        if (Interlocked.Exchange(ref _ending, 1) != 0)
        {
            await Ended.ConfigureAwait(false);
            return;
        }
        Volatile.Write(ref _state, Ending);
        Volatile.Write(ref _pendingState, null);
        _logger.LogInformation("Connection {Number} closed: {Reason}", Number, logReason);
        StopHeartbeat();
        _helloWatch?.Dispose();
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposed already.
        }
        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            _logger.LogDebug("Connection {Number}: closing failed ({Error})", Number, RemoteLog.Failure(ex));
        }
        Volatile.Read(ref _keys)?.Dispose();
        _ended.TrySetResult();
    }

    private static bool IsConnectionFailure(Exception ex) => ex is IOException or ObjectDisposedException or OperationCanceledException;
}
