using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Granary.App.Services;
using Granary.Core.Remote;
using Granary.Tests.Fakes;

namespace Granary.Tests.Remote;

/// <summary>
/// The App's listener for phones, apart from its socket: its accept loop and the connections it hands over, linked in
/// from <c>Granary.App/Services</c>. No test opens a socket: accepts come from a source the test feeds, and a connection's
/// socket is one end of a <see cref="DuplexPipe"/>. The rest of <c>TcpRemoteListener</c> (binding, the port) is read by
/// hand (Review Focus 3).
/// </summary>
public sealed class RemoteAcceptLoopTests : IDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private static readonly IPAddress PhoneAddress = IPAddress.Parse("192.168.1.30");

    private readonly LogSink _logs = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<DuplexPipe> _pipes = [];
    private readonly List<AcceptSource> _sources = [];
    private readonly List<RemoteAcceptLoop> _loops = [];
    private int _failures;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var loop in _loops)
        {
            loop.Stop();
        }
        foreach (var source in _sources)
        {
            source.Close();
        }
        foreach (var pipe in _pipes)
        {
            pipe.Dispose();
        }
    }

    [Fact]
    public async Task Stop_ReturnsAtOnce_WhileAnAcceptIsPending_ThatHeedsNoStop()
    {
        var source = Source(heedsStop: false);
        var loop = Loop(source, _ => Task.CompletedTask);
        loop.Start();
        await source.UntilAcceptingAsync(1);

        await Task.Run(loop.Stop, Token).WaitAsync(HangGuard, Token);

        Assert.False(loop.Running.IsCompleted);
    }

    [Fact]
    public async Task Stop_ReturnsAtOnce_WhileAConnectionHandedOverStillRuns()
    {
        var source = Source();
        var handed = new List<IRemoteConnection>();
        var loop = Loop(source, connection =>
        {
            lock (handed)
            {
                handed.Add(connection);
            }
            // The connection's task runs as long as the connection.
            return new TaskCompletionSource().Task;
        });
        loop.Start();
        var connection = NewConnection();
        source.Give(connection);
        await source.UntilAcceptingAsync(2);

        await Task.Run(loop.Stop, Token).WaitAsync(HangGuard, Token);
        await loop.Running.WaitAsync(HangGuard, Token);

        Assert.Same(connection, Assert.Single(handed));
        // It stays with its owner.
        Assert.False(connection.IsDisposed);
    }

    [Fact]
    public async Task EveryConnection_IsHandedOver_WithoutWaitingForTheOneBefore()
    {
        var source = Source();
        int handed = 0;
        var loop = Loop(source, _ =>
        {
            Interlocked.Increment(ref handed);
            return new TaskCompletionSource().Task;
        });
        loop.Start();

        source.Give(NewConnection());
        source.Give(NewConnection());
        await source.UntilAcceptingAsync(3);

        Assert.Equal(2, Volatile.Read(ref handed));
    }

    [Fact]
    public async Task AConnectionThatFailsWhileItIsAccepted_DoesNotStopTheLoop()
    {
        var source = Source();
        var handed = new TaskCompletionSource<IRemoteConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = Loop(source, connection =>
        {
            handed.TrySetResult(connection);
            return Task.CompletedTask;
        });
        loop.Start();

        source.Fail(new SocketException((int)SocketError.ConnectionReset));
        source.Fail(new IOException("The phone at 192.168.1.30 went away."));
        var connection = NewConnection();
        source.Give(connection);
        // The second failure in a row is waited out.
        Assert.Equal(RemoteAcceptLoop.FirstBackoff, await _time.FireNextTimerAsync(Token));

        Assert.Same(connection, await handed.Task.WaitAsync(HangGuard, Token));
        Assert.False(loop.Running.IsCompleted);
        Assert.DoesNotContain("192.168.1.30", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABurstOfConnectionsThatFail_NeverStopsTheLoop_AndTheNextGoodOneIsTaken()
    {
        // A peer on the network that makes accept after accept fail, for less than GiveUpAfter.
        const int Burst = 20;
        var source = Source();
        var handed = new TaskCompletionSource<IRemoteConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = Loop(source, connection =>
        {
            handed.TrySetResult(connection);
            return Task.CompletedTask;
        });
        loop.Start();
        for (int i = 0; i < Burst; i++)
        {
            source.Fail(i % 2 == 0 ? new SocketException((int)SocketError.ConnectionReset) : new IOException("The phone at 192.168.1.30 went away."));
        }
        var good = NewConnection();
        source.Give(good);

        // Failures back to back are waited out, a little longer each time but never longer than a second.
        var waits = new List<TimeSpan>();
        while (!handed.Task.IsCompleted)
        {
            await Task.WhenAny(handed.Task, _time.WaitForTimerAsync(Token)).WaitAsync(HangGuard, Token);
            if (_time.NextDue is { } due)
            {
                waits.Add(due);
                _time.Advance(due);
            }
        }

        Assert.Same(good, await handed.Task);
        // And it goes on accepting.
        await source.UntilAcceptingAsync(Burst + 2);
        Assert.False(loop.Running.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref _failures));
        Assert.Equal(Burst - 1, waits.Count);
        Assert.Equal(RemoteAcceptLoop.FirstBackoff, waits[0]);
        Assert.All(waits, wait => Assert.InRange(wait, RemoteAcceptLoop.FirstBackoff, RemoteAcceptLoop.LongestBackoff));
        Assert.Equal(RemoteAcceptLoop.LongestBackoff, waits[^1]);
        // Logged when the run began and when it ended, not once a failure.
        Assert.Single(_logs.Lines, line => line.Contains("ended while it was accepted", StringComparison.Ordinal));
        Assert.Single(_logs.Lines, line => line.Contains($"{Burst} connections in a row", StringComparison.Ordinal));
        Assert.DoesNotContain("192.168.1.30", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedConnectionsThatGoOnForHalfAMinute_AreTheListenersOwnFailure()
    {
        // Out of sockets or buffers for good: no connection gets through, and the port must not stay advertised.
        var source = Source();
        var loop = Loop(source, _ => Task.CompletedTask);
        loop.Start();
        for (int i = 0; i < 100; i++)
        {
            source.Fail(new SocketException((int)SocketError.TooManyOpenSockets));
        }

        await FireTimersUntilAsync(loop.Running);

        Assert.Equal(1, Volatile.Read(ref _failures));
        Assert.InRange(_time.Elapsed, RemoteAcceptLoop.GiveUpAfter, RemoteAcceptLoop.GiveUpAfter + RemoteAcceptLoop.LongestBackoff);
        Assert.Contains("stopped taking connections", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PeersWhoseConnectionsFailForMoreThanHalfAMinute_NeverStopTheLoop()
    {
        // Resets and broken connections are the peers', however long they go on: a phone that comes after them still gets in.
        var source = Source();
        var handed = new TaskCompletionSource<IRemoteConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = Loop(source, connection =>
        {
            handed.TrySetResult(connection);
            return Task.CompletedTask;
        });
        loop.Start();
        for (int i = 0; i < 100; i++)
        {
            source.Fail(i % 2 == 0 ? new SocketException((int)SocketError.ConnectionReset) : new IOException("The peer went away."));
        }
        var good = NewConnection();
        source.Give(good);

        await FireTimersUntilAsync(handed.Task);

        Assert.Same(good, await handed.Task);
        Assert.True(_time.Elapsed > RemoteAcceptLoop.GiveUpAfter);
        Assert.Equal(0, Volatile.Read(ref _failures));
        Assert.False(loop.Running.IsCompleted);
    }

    [Fact]
    public async Task AFewShortagesAmongAPeersResets_DoNotStopTheLoop()
    {
        // A shortage, then a peer's resets for longer than the half minute, then another shortage: two are no lasting shortage.
        var source = Source();
        var handed = new TaskCompletionSource<IRemoteConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = Loop(source, connection =>
        {
            handed.TrySetResult(connection);
            return Task.CompletedTask;
        });
        loop.Start();
        source.Fail(new SocketException((int)SocketError.TooManyOpenSockets));
        for (int i = 0; i < 60; i++)
        {
            source.Fail(new SocketException((int)SocketError.ConnectionReset));
        }
        source.Fail(new SocketException((int)SocketError.TooManyOpenSockets));
        var good = NewConnection();
        source.Give(good);

        await FireTimersUntilAsync(handed.Task);

        Assert.Same(good, await handed.Task);
        Assert.True(_time.Elapsed > RemoteAcceptLoop.GiveUpAfter);
        Assert.Equal(0, Volatile.Read(ref _failures));
    }

    [Fact]
    public async Task ALastingShortage_StopsTheLoop_AlsoWithAPeersResetsAmongIt()
    {
        // A peer's resets between the shortages must not hide that this PC is out of sockets: the port must not stay advertised.
        var source = Source();
        var loop = Loop(source, _ => Task.CompletedTask);
        loop.Start();
        for (int i = 0; i < 100; i++)
        {
            source.Fail(i % 2 == 0 ? new SocketException((int)SocketError.TooManyOpenSockets) : new SocketException((int)SocketError.ConnectionReset));
        }

        await FireTimersUntilAsync(loop.Running);

        Assert.Equal(1, Volatile.Read(ref _failures));
        Assert.True(_time.Elapsed >= RemoteAcceptLoop.GiveUpAfter);
    }

    [Fact]
    public async Task ShortagesFarApart_AmongAPeersResets_NeverStopTheLoop()
    {
        // A shortage now and then over minutes is no lasting one, however many come together in the end.
        var source = Source();
        var handed = new TaskCompletionSource<IRemoteConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = Loop(source, connection =>
        {
            handed.TrySetResult(connection);
            return Task.CompletedTask;
        });
        loop.Start();
        for (int run = 0; run < 2 * RemoteAcceptLoop.ShortagesToGiveUp; run++)
        {
            source.Fail(new SocketException((int)SocketError.TooManyOpenSockets));
            for (int i = 0; i < 40; i++)
            {
                source.Fail(new SocketException((int)SocketError.ConnectionReset));
            }
        }
        var good = NewConnection();
        source.Give(good);

        await FireTimersUntilAsync(handed.Task);

        Assert.Same(good, await handed.Task);
        Assert.Equal(0, Volatile.Read(ref _failures));
    }

    [Fact]
    public async Task AGoodConnection_StartsTheHalfMinuteAnew()
    {
        var source = Source();
        int handed = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = Loop(source, _ =>
        {
            if (Interlocked.Increment(ref handed) == 2)
            {
                second.TrySetResult();
            }
            return Task.CompletedTask;
        });
        loop.Start();
        // Twice 20 seconds and more of failures, a good connection between them.
        for (int run = 0; run < 2; run++)
        {
            for (int i = 0; i < 25; i++)
            {
                source.Fail(new SocketException((int)SocketError.TooManyOpenSockets));
            }
            source.Give(NewConnection());
        }

        await FireTimersUntilAsync(second.Task);

        Assert.True(_time.Elapsed > RemoteAcceptLoop.GiveUpAfter);
        Assert.Equal(0, Volatile.Read(ref _failures));
        Assert.False(loop.Running.IsCompleted);
    }

    // Moves the clock to each timer the loop sets, until done ends.
    private async Task FireTimersUntilAsync(Task done)
    {
        while (!done.IsCompleted)
        {
            await Task.WhenAny(done, _time.WaitForTimerAsync(Token)).WaitAsync(HangGuard, Token);
            if (!done.IsCompleted && _time.NextDue is { } due)
            {
                _time.Advance(due);
            }
        }
    }

    [Fact]
    public async Task AStopDuringTheWaitAfterFailures_EndsTheLoop_AsNoFailure()
    {
        var source = Source();
        var loop = Loop(source, _ => Task.CompletedTask);
        loop.Start();
        source.Fail(new SocketException((int)SocketError.ConnectionReset));
        source.Fail(new SocketException((int)SocketError.ConnectionReset));
        await _time.WaitForTimerAsync(Token);

        loop.Stop();

        await loop.Running.WaitAsync(HangGuard, Token);
        Assert.Equal(0, Volatile.Read(ref _failures));
    }

    [Fact]
    public async Task AFailureOfTheListenerItself_EndsTheLoop_TellsItsOwner_AndNamesNoAddress()
    {
        var source = Source();
        var loop = Loop(source, _ => Task.CompletedTask);
        loop.Start();

        source.Fail(new InvalidOperationException("The listener on 192.168.1.20:52817 is broken."));

        await loop.Running.WaitAsync(HangGuard, Token);
        Assert.Equal(1, source.Accepts);
        Assert.Equal(1, Volatile.Read(ref _failures));
        Assert.Contains("stopped taking connections", _logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.1.20", _logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain("52817", _logs.All, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SocketError.NotSocket)]
    [InlineData(SocketError.InvalidArgument)]
    [InlineData(SocketError.NetworkDown)]
    public async Task ASocketErrorOfTheListener_EndsTheLoop_AndTellsItsOwner(SocketError error)
    {
        var source = Source();
        var loop = Loop(source, _ => Task.CompletedTask);
        loop.Start();

        source.Fail(new SocketException((int)error));

        await loop.Running.WaitAsync(HangGuard, Token);
        Assert.Equal(1, Volatile.Read(ref _failures));
    }

    [Fact]
    public async Task AStop_IsNoFailure()
    {
        var source = Source();
        var loop = Loop(source, _ => Task.CompletedTask);
        loop.Start();
        await source.UntilAcceptingAsync(1);

        loop.Stop();

        await loop.Running.WaitAsync(HangGuard, Token);
        Assert.Equal(0, Volatile.Read(ref _failures));
    }

    [Fact]
    public async Task AConnectionAcceptedAsTheLoopStops_IsClosed_NotHandedOver()
    {
        var source = Source(heedsStop: false);
        int handed = 0;
        var loop = Loop(source, _ =>
        {
            Interlocked.Increment(ref handed);
            return Task.CompletedTask;
        });
        loop.Start();
        await source.UntilAcceptingAsync(1);
        loop.Stop();

        var late = NewConnection();
        source.Give(late);

        await loop.Running.WaitAsync(HangGuard, Token);
        Assert.Equal(0, Volatile.Read(ref handed));
        Assert.True(late.IsDisposed);
    }

    [Fact]
    public async Task AHandOverThatThrows_ClosesThatConnection_AndTheLoopGoesOn()
    {
        var source = Source();
        var second = new TaskCompletionSource<IRemoteConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var loop = Loop(source, connection =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("The phone at 192.168.1.30 could not be served.");
            }
            second.TrySetResult(connection);
            return Task.CompletedTask;
        });
        loop.Start();
        var first = NewConnection();

        source.Give(first);
        var next = NewConnection();
        source.Give(next);

        Assert.Same(next, await second.Task.WaitAsync(HangGuard, Token));
        Assert.True(first.IsDisposed);
        Assert.DoesNotContain("192.168.1.30", _logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public void Stop_IsSafeTwice_AndBeforeStart()
    {
        var loop = Loop(Source(), _ => Task.CompletedTask);

        loop.Stop();
        loop.Stop();
        loop.Start();

        Assert.True(loop.Running.IsCompleted);
    }

    [Fact]
    public async Task AConnectionsDispose_EndsAReadAndAWriteInFlight_AtOnce()
    {
        var pipe = NewPipe(pauseAt: 1024);
        var socket = new FakeSocket(pipe.Receiver);
        var connection = new AcceptedConnection(pipe.Receiver, PhoneAddress, socket);
        // Nothing comes from the phone, and it reads nothing: the read and the write both wait.
        pipe.PhoneStopsReading();
        var reading = connection.Stream.ReadAsync(new byte[16], Token).AsTask();
        var writing = connection.Stream.WriteAsync(new byte[64 * 1024], Token).AsTask();
        Assert.False(reading.IsCompleted);
        Assert.False(writing.IsCompleted);

        var disposing = connection.DisposeAsync();

        Assert.True(disposing.IsCompleted);
        Assert.True(socket.IsClosed);
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => reading.WaitAsync(HangGuard, Token));
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => writing.WaitAsync(HangGuard, Token));
        Assert.True(failure is IOException or ObjectDisposedException, failure.GetType().Name);
    }

    [Fact]
    public async Task AConnectionsDispose_IsSafeTwice_AndNamesItsPeer()
    {
        var pipe = NewPipe();
        var socket = new FakeSocket(pipe.Receiver);
        var connection = new AcceptedConnection(pipe.Receiver, PhoneAddress, socket);
        Assert.Equal(PhoneAddress, connection.Peer);

        await connection.DisposeAsync();
        await connection.DisposeAsync();

        Assert.Equal(1, socket.Closes);
    }

    [Fact]
    public async Task AConnectionsDispose_EndsTheSendingFirst_SoWhatWasWrittenLeavesBeforeTheClose()
    {
        // With bytes from the phone still unread, a close alone would reset the connection and drop a close frame not sent yet.
        var pipe = NewPipe();
        var socket = new FakeSocket(pipe.Receiver);
        var connection = new AcceptedConnection(pipe.Receiver, PhoneAddress, socket);

        await connection.DisposeAsync();

        Assert.Equal(["shutdown send", "close"], socket.Steps);
    }

    [Fact]
    public async Task AConnectionsDispose_StillCloses_WhenItsSendingCannotBeEnded()
    {
        var pipe = NewPipe();
        var socket = new FakeSocket(pipe.Receiver) { ShutdownFails = new SocketException((int)SocketError.NotConnected) };
        var connection = new AcceptedConnection(pipe.Receiver, PhoneAddress, socket);

        var disposing = connection.DisposeAsync();

        Assert.True(disposing.IsCompleted);
        await disposing;
        Assert.Equal(["shutdown send", "close"], socket.Steps);
    }

    private AcceptSource Source(bool heedsStop = true)
    {
        var source = new AcceptSource(heedsStop);
        _sources.Add(source);
        return source;
    }

    private RemoteAcceptLoop Loop(AcceptSource source, Func<IRemoteConnection, Task> onAccepted)
    {
        var loop = new RemoteAcceptLoop(source.AcceptAsync, onAccepted, () => Interlocked.Increment(ref _failures), _time,
            _logs.For<RemoteAcceptLoop>());
        _loops.Add(loop);
        return loop;
    }

    private DuplexPipe NewPipe(long pauseAt = 64 * 1024)
    {
        var pipe = new DuplexPipe(pauseAt);
        _pipes.Add(pipe);
        return pipe;
    }

    private DuplexPipe.PipeConnection NewConnection() => NewPipe().Connection(PhoneAddress);

    /// <summary>
    /// Accepts as the test hands them out, one per accept, in order: a connection, or a failure. With nothing handed out an
    /// accept waits, and ends at the loop's stop only if the source heeds it.
    /// </summary>
    private sealed class AcceptSource(bool heedsStop)
    {
        private readonly Channel<object> _next = Channel.CreateUnbounded<object>();
        private readonly Lock _gate = new();
        private TaskCompletionSource _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _accepts;

        public int Accepts => Volatile.Read(ref _accepts);

        public void Give(IRemoteConnection connection) => _next.Writer.TryWrite(connection);

        public void Fail(Exception failure) => _next.Writer.TryWrite(failure);

        public void Close() => _next.Writer.TryComplete();

        public async ValueTask<IRemoteConnection> AcceptAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _accepts);
            TaskCompletionSource accepted;
            lock (_gate)
            {
                accepted = _accepted;
                _accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            accepted.TrySetResult();
            object next = await _next.Reader.ReadAsync(heedsStop ? cancellationToken : CancellationToken.None);
            return next is Exception failure ? throw failure : (IRemoteConnection)next;
        }

        /// <summary>Completes once the loop has asked for <paramref name="count"/> accepts.</summary>
        public async Task UntilAcceptingAsync(int count)
        {
            while (true)
            {
                Task accepted;
                lock (_gate)
                {
                    accepted = _accepted.Task;
                }
                if (Accepts >= count)
                {
                    return;
                }
                await accepted.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
            }
        }
    }

    /// <summary>
    /// A connection's socket: closing it ends what is in flight on the stream, as closing a real socket does. It notes the
    /// end of sending and the close in order, and its end of sending can be told to fail.
    /// </summary>
    private sealed class FakeSocket(Stream stream) : IConnectionSocket
    {
        private readonly List<string> _steps = [];
        private int _closes;

        public int Closes => Volatile.Read(ref _closes);

        public bool IsClosed => Closes > 0;

        public Exception? ShutdownFails { get; set; }

        public IReadOnlyList<string> Steps
        {
            get
            {
                lock (_steps)
                {
                    return [.. _steps];
                }
            }
        }

        public void ShutdownSend()
        {
            lock (_steps)
            {
                _steps.Add("shutdown send");
            }
            if (ShutdownFails is { } failure)
            {
                throw failure;
            }
        }

        public void Dispose()
        {
            lock (_steps)
            {
                _steps.Add("close");
            }
            Interlocked.Increment(ref _closes);
            stream.Dispose();
        }
    }
}
