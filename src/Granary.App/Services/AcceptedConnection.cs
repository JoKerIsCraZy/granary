using System.Net;
using System.Net.Sockets;
using Granary.Core.Remote;

namespace Granary.App.Services;

/// <summary>
/// A connection <see cref="TcpRemoteListener"/> accepted: the socket's stream and the phone's address. Disposing it closes
/// the socket itself, not only the stream, so a read or write in flight ends at once with an <see cref="IOException"/> or
/// <see cref="ObjectDisposedException"/>; it waits for nothing, the phone least of all (<see cref="IRemoteConnection"/>).
/// Windows sends what was written before in the background, a <c>close</c> frame included.
/// </summary>
internal sealed class AcceptedConnection : IRemoteConnection
{
    private readonly IDisposable _socket;
    private int _disposed;

    /// <param name="stream">The socket's stream.</param>
    /// <param name="peer">The address the phone connected from.</param>
    /// <param name="socket">The socket under the stream; a test gives a stand-in.</param>
    public AcceptedConnection(Stream stream, IPAddress peer, IDisposable socket)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(socket);
        Stream = stream;
        Peer = peer;
        _socket = socket;
    }

    public Stream Stream { get; }

    public IPAddress Peer { get; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }
        // The socket first: closing it aborts what is pending on it. Neither call waits for the phone.
        try
        {
            _socket.Dispose();
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Closed already.
        }
        try
        {
            Stream.Dispose();
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // The socket under it is gone already.
        }
        return ValueTask.CompletedTask;
    }

    public override string ToString() => nameof(AcceptedConnection);
}
