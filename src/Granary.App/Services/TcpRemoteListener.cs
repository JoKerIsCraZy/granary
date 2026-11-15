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
/// handed over stay with the receiver. One listener at a time; a start stops the one before.
/// </summary>
public sealed class TcpRemoteListener(ILogger<TcpRemoteListener> logger) : IRemoteListener
{
    private const int Backlog = 4;

    private readonly Lock _gate = new();
    private Listening? _listening;

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
        var loop = new RemoteAcceptLoop(token => AcceptAsync(listener, token), onAccepted, logger);
        lock (_gate)
        {
            _listening = new Listening(listener, loop);
        }
        loop.Start();
        return Task.FromResult(port);
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
            return new AcceptedConnection(new NetworkStream(socket, ownsSocket: true), peer, socket);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // A peer that went away as it was accepted: that connection failed, not the listener.
            socket.Dispose();
            throw new IOException("A connection ended as it was accepted.", ex);
        }
    }

    private sealed record Listening(TcpListener Listener, RemoteAcceptLoop Loop);
}
