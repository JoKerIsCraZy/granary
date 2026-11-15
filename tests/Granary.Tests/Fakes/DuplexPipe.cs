using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using Granary.Core.Remote;

namespace Granary.Tests.Fakes;

/// <summary>
/// A connection between the receiver and a phone in memory: two pipes, one each way, as two streams. No socket is
/// opened. The phone's side can be told to stop reading; what is written to it then stays in its pipe, and once that pipe
/// holds <c>pauseAt</c> bytes the receiver's write waits, as a socket's does when its peer reads nothing. The way back
/// to the receiver always has room.
/// </summary>
internal sealed class DuplexPipe : IDisposable
{
    private readonly PipeStream _receiver;
    private readonly PipeStream _phone;

    public DuplexPipe(long pauseAt = 64 * 1024)
    {
        var toPhone = new Pipe(new PipeOptions(pauseWriterThreshold: pauseAt, resumeWriterThreshold: Math.Max(1, pauseAt / 2),
            useSynchronizationContext: false));
        var toReceiver = new Pipe(new PipeOptions(pauseWriterThreshold: 1024 * 1024, resumeWriterThreshold: 512 * 1024,
            useSynchronizationContext: false));
        _receiver = new PipeStream(toReceiver.Reader, toPhone.Writer);
        _phone = new PipeStream(toPhone.Reader, toReceiver.Writer);
    }

    /// <summary>The receiver's end: what the phone wrote, and the way to it.</summary>
    public Stream Receiver => _receiver;

    /// <summary>The phone's end.</summary>
    public Stream Phone => _phone;

    /// <summary>
    /// The receiver's end as an accepted connection from <paramref name="peer"/> (a LAN address unless given). Closing it
    /// waits for <paramref name="closeWaitsFor"/> first, if a test holds it up.
    /// </summary>
    public PipeConnection Connection(IPAddress? peer = null, Task? closeWaitsFor = null) =>
        new(_receiver, peer ?? IPAddress.Parse("192.168.1.30"), closeWaitsFor ?? Task.CompletedTask);

    /// <summary>The phone takes nothing more from its pipe until <see cref="PhoneReadsAgain"/>.</summary>
    public void PhoneStopsReading() => _phone.Pause();

    public void PhoneReadsAgain() => _phone.Resume();

    public void Dispose()
    {
        _receiver.Dispose();
        _phone.Dispose();
    }

    /// <summary>An accepted connection over one end of the pipe; disposing it closes that end, as closing a socket does.</summary>
    internal sealed class PipeConnection(Stream stream, IPAddress peer, Task closeWaitsFor) : IRemoteConnection
    {
        private int _disposed;

        public Stream Stream => stream;

        public IPAddress Peer => peer;

        public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

        public async ValueTask DisposeAsync()
        {
            await closeWaitsFor;
            Interlocked.Exchange(ref _disposed, 1);
            await stream.DisposeAsync();
        }
    }

    // One end: reads from one pipe and writes to the other. Closing it ends both directions for the other end: its reads
    // see the end of the stream and its writes fail, as with a closed socket.
    private sealed class PipeStream(PipeReader reader, PipeWriter writer) : Stream
    {
        private readonly Lock _gate = new();
        private TaskCompletionSource? _paused;
        private int _closed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Pause()
        {
            lock (_gate)
            {
                _paused ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Resume()
        {
            TaskCompletionSource? paused;
            lock (_gate)
            {
                paused = _paused;
                _paused = null;
            }
            paused?.TrySetResult();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) == 1, this);
                ReadResult result;
                try
                {
                    result = await reader.ReadAsync(cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    throw new ObjectDisposedException(nameof(PipeStream));
                }
                if (result.IsCanceled)
                {
                    throw new ObjectDisposedException(nameof(PipeStream));
                }
                var data = result.Buffer;
                Task? paused;
                lock (_gate)
                {
                    paused = _paused?.Task;
                }
                try
                {
                    if (paused is not null)
                    {
                        // Not taken: the bytes stay in the pipe, count against the writer and are read after Resume.
                        reader.AdvanceTo(data.Start);
                        await paused.WaitAsync(cancellationToken);
                        continue;
                    }
                    if (data.IsEmpty && result.IsCompleted)
                    {
                        reader.AdvanceTo(data.End);
                        return 0;
                    }
                    int count = (int)Math.Min(data.Length, buffer.Length);
                    data.Slice(0, count).CopyTo(buffer.Span);
                    reader.AdvanceTo(data.GetPosition(count));
                    return count;
                }
                catch (InvalidOperationException)
                {
                    // Closed under the read.
                    throw new ObjectDisposedException(nameof(PipeStream));
                }
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _closed) == 1)
            {
                throw new ObjectDisposedException(nameof(PipeStream));
            }
            FlushResult result;
            try
            {
                result = await writer.WriteAsync(buffer, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                throw new IOException("The connection was closed.");
            }
            if (result.IsCanceled || result.IsCompleted)
            {
                throw new IOException("The connection was closed.");
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Read asynchronously.");

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Write asynchronously.");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
            {
                reader.CancelPendingRead();
                writer.CancelPendingFlush();
                Resume();
                writer.Complete();
                reader.Complete();
            }
            base.Dispose(disposing);
        }
    }
}
