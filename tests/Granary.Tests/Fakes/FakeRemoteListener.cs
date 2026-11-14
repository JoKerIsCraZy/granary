using System.Net;
using Granary.Core.Remote;

namespace Granary.Tests.Fakes;

/// <summary>The calls the receiver made to its listener and its advertiser, in one list, in order.</summary>
internal sealed class RemoteCalls
{
    private readonly List<string> _calls = [];

    public void Add(string call)
    {
        lock (_calls)
        {
            _calls.Add(call);
        }
    }

    public IReadOnlyList<string> All
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }
}

/// <summary>
/// A listener that binds nothing: it notes each start and stop, answers the port a test chooses, and lets a test hand
/// the receiver a connection as if a phone had connected.
/// </summary>
internal sealed class FakeRemoteListener(RemoteCalls calls) : IRemoteListener
{
    private Func<IRemoteConnection, Task>? _onAccepted;

    /// <summary>The port bound from the ports offered: the first, or 49152 for "any".</summary>
    public Func<IReadOnlyList<int>, int> Binds { get; set; } = ports => ports[0] == 0 ? 49152 : ports[0];

    /// <summary>No port is free: a start throws <see cref="IOException"/>.</summary>
    public bool NoPortFree { get; set; }

    public IReadOnlyList<int> LastPorts { get; private set; } = [];

    public IPAddress? Address { get; private set; }

    public bool IsListening => Volatile.Read(ref _onAccepted) is not null;

    public int Starts { get; private set; }

    public Task<int> StartAsync(IPAddress address, IReadOnlyList<int> ports, Func<IRemoteConnection, Task> onAccepted, CancellationToken cancellationToken)
    {
        calls.Add($"listener.Start({address})");
        LastPorts = [.. ports];
        Address = address;
        if (NoPortFree)
        {
            return Task.FromException<int>(new IOException("No port is free."));
        }
        Starts++;
        Volatile.Write(ref _onAccepted, onAccepted);
        return Task.FromResult(Binds(ports));
    }

    public Task StopAsync()
    {
        calls.Add("listener.Stop");
        Volatile.Write(ref _onAccepted, null);
        return Task.CompletedTask;
    }

    /// <summary>A phone connected: the receiver serves <paramref name="connection"/>; the task ends with the connection.</summary>
    public Task AcceptAsync(IRemoteConnection connection) =>
        (Volatile.Read(ref _onAccepted) ?? throw new InvalidOperationException("Nothing listens."))(connection);
}
