using System.Net;

namespace Granary.Core.Remote;

/// <summary>
/// One connection a phone opened to the receiver. Disposing it closes it, as closing a socket does: a read or write in
/// progress on <see cref="Stream"/> then fails, which is how a connection is ended rather than by cancelling a read.
/// </summary>
public interface IRemoteConnection : IAsyncDisposable
{
    /// <summary>The connection's bytes, both ways.</summary>
    Stream Stream { get; }

    /// <summary>The address the phone connected from.</summary>
    IPAddress Peer { get; }
}

/// <summary>
/// Listens for phones on one LAN address. The App implements it with a <c>TcpListener</c>; Core never opens a socket.
/// </summary>
public interface IRemoteListener
{
    /// <summary>
    /// Binds the first port of <paramref name="ports"/> that is free on <paramref name="address"/> (0 for any port Windows
    /// gives) and hands each accepted connection to <paramref name="onAccepted"/>. Answers the port bound; throws
    /// <see cref="IOException"/> when none could be.
    /// </summary>
    Task<int> StartAsync(IPAddress address, IReadOnlyList<int> ports, Func<IRemoteConnection, Task> onAccepted, CancellationToken cancellationToken);

    /// <summary>Stops accepting and closes the listener. Connections already handed over stay with their owner.</summary>
    Task StopAsync();
}
