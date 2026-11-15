using System.Net;
using System.Net.Sockets;
using Granary.Core.Remote;
using Microsoft.Extensions.Logging;

namespace Granary.App.Services;

/// <summary>
/// The phone remote's listener (<see cref="IRemoteListener"/>): a <see cref="TcpListener"/> on the one LAN address the
/// receiver names, IPv4 only and never on every address, on the first port of the receiver's list that binds, with a
/// backlog of 4 (the receiver takes no more connections than that before a hello). Its accept loop
/// (<see cref="RemoteAcceptLoop"/>) hands each connection over with Nagle off, since frames are small and a command should
/// not wait for more. <see cref="StopAsync"/> never waits: it stops the loop and closes the listener, and connections
/// handed over stay with the receiver. One listener at a time; a start stops the one before. A connection that fails as
/// it is accepted never ends the loop; a failure of the listening socket itself does, and raises <see cref="Failed"/>, so
/// the receiver stops and takes its advertisement back.
/// </summary>
public sealed class TcpRemoteListener(TimeProvider time, ILogger<TcpRemoteListener> logger) : IRemoteListener
{
    private const int Backlog = 4;

    private readonly Lock _gate = new();
    private Listening? _listening;

    public event EventHandler? Failed;

    public Task<int> StartAsync(IPAddress address, IReadOnlyList<int> ports, Func<IRemoteConnection, Task> onAccepted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(onAccepted);
        if (address.AddressFamily != AddressFamily.InterNetwork || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast))
        {
            throw new ArgumentException("The phone remote listens on one IPv4 address of this PC.", nameof(address));
        }
        cancellationToken.ThrowIfCancellationRequested();
        StopListening();
        // The message names no address or port: the receiver logs an exception by its type and code, but a message travels.
        var listener = Bind(address, ports) ?? throw new IOException("No port of the phone remote's list was free.");
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        RemoteAcceptLoop? loop = null;
        loop = new RemoteAcceptLoop(token => AcceptAsync(listener, token), onAccepted, () => OnLoopFailed(loop!), time, logger);
        lock (_gate)
        {
            _listening = new Listening(listener, loop);
        }
        loop.Start();
        return Task.FromResult(port);
    }

    // The loop of the listener that runs now ended by itself: the receiver is told. One stopped or replaced meanwhile is not news.
    private void OnLoopFailed(RemoteAcceptLoop loop)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_listening?.Loop, loop))
            {
                return;
            }
        }
        Failed?.Invoke(this, EventArgs.Empty);
    }

    public Task StopAsync()
    {
        StopListening();
        return Task.CompletedTask;
    }

    // Stops the loop, then closes the listener, which ends an accept in flight; neither waits.
    private void StopListening()
    {
        Listening? listening;
        lock (_gate)
        {
            listening = _listening;
            _listening = null;
        }
        if (listening is null)
        {
            return;
        }
        listening.Loop.Stop();
        try
        {
            listening.Listener.Dispose();
        }
        catch (SocketException ex)
        {
            logger.LogDebug("The phone remote's listener did not close cleanly ({Error})", RemoteLog.Failure(ex));
        }
    }

    // The first port that binds, or null. A port Windows refuses is tried no further.
    private TcpListener? Bind(IPAddress address, IReadOnlyList<int> ports)
    {
        foreach (int port in ports)
        {
            var listener = new TcpListener(address, port);
            try
            {
                listener.Start(Backlog);
                return listener;
            }
            catch (SocketException ex)
            {
                listener.Dispose();
                logger.LogDebug("Port {Port} was not free for the phone remote ({Error})", port, RemoteLog.Failure(ex));
            }
        }
        return null;
    }

    private static async ValueTask<IRemoteConnection> AcceptAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        var socket = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            socket.NoDelay = true;
            var peer = (socket.RemoteEndPoint as IPEndPoint)?.Address ?? throw new SocketException((int)SocketError.NotConnected);
            // Throws IOException for a socket that is no longer connected: a peer that connected and reset at once.
            return new AcceptedConnection(new NetworkStream(socket, ownsSocket: true), peer, socket);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever failed after the accept, the socket is closed here and now, not left to the finalizer: a host on the
            // network that connects and resets again and again must not pile up handles. The listener is fine: that one
            // connection failed.
            socket.Dispose();
            throw new IOException("A connection ended as it was accepted.", ex);
        }
    }

    private sealed record Listening(TcpListener Listener, RemoteAcceptLoop Loop);
}
