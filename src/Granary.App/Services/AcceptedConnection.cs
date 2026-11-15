using System.Net;
using System.Net.Sockets;
using Granary.Core.Remote;

namespace Granary.App.Services;

/// <summary>
/// A connection <see cref="TcpRemoteListener"/> accepted: the socket's stream and the phone's address. Disposing it ends
/// the sending first, so what was written (a <c>close</c> frame) leaves before the end, then closes the socket itself, not
/// only the stream, so a read or write in flight ends at once with an <see cref="IOException"/> or
/// <see cref="ObjectDisposedException"/>. It waits for nothing, the phone least of all (<see cref="IRemoteConnection"/>).
/// </summary>
internal sealed class AcceptedConnection : IRemoteConnection
{
    private readonly IConnectionSocket _socket;
    private int _disposed;

    /// <param name="stream">The socket's stream.</param>
    /// <param name="peer">The address the phone connected from.</param>
    /// <param name="socket">The socket under the stream; a test gives a stand-in.</param>
    public AcceptedConnection(Stream stream, IPAddress peer, IConnectionSocket socket)
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
        // The end of sending first: closed with bytes from the phone still unread, the socket would reset the connection
        // and drop what was not sent yet, the close frame that tells the phone why it is let go.
        try
        {
            _socket.ShutdownSend();
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Gone already.
        }
        // Then the socket: closing it aborts what is pending on it. None of these waits for the phone.
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

/// <summary>The socket under an accepted connection, as far as ending it goes.</summary>
internal interface IConnectionSocket : IDisposable
{
    /// <summary>Ends the sending (<c>shutdown</c> with <c>SD_SEND</c>): what was written leaves, then the end. Does not wait.</summary>
    void ShutdownSend();
}

/// <summary>A real socket as an <see cref="IConnectionSocket"/>.</summary>
internal sealed class SocketEnd(Socket socket) : IConnectionSocket
{
    public void ShutdownSend() => socket.Shutdown(SocketShutdown.Send);

    public void Dispose() => socket.Dispose();
}
