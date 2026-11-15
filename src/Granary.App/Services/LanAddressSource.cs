using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Granary.Core.Remote;
using Microsoft.Extensions.Logging;

namespace Granary.App.Services;

/// <summary>
/// This PC's address on its local network for the phone remote, chosen by <see cref="LanAddressRule"/> from Windows'
/// adapters. While anyone follows <see cref="Changed"/> it is read off the UI thread, once at the first handler and again
/// whenever Windows says an address changed (<see cref="NetworkChange.NetworkAddressChanged"/>, followed with the first
/// handler and left with the last, as Core's <c>LocalNetwork</c> does), and <see cref="Current"/> answers what was read
/// last. The address is never logged.
/// </summary>
public sealed class LanAddressSource(ILogger<LanAddressSource> logger) : ILanAddressSource, IDisposable
{
    private readonly Lock _gate = new();
    private EventHandler? _changed;
    private LanAddress? _current;
    // Reads are numbered, so an older read that ends late never replaces a newer one.
    private int _reads;
    private int _stored;

    /// <summary>
    /// The address read last while <see cref="Changed"/> is followed (null until the first read is in); otherwise read
    /// now.
    /// </summary>
    public LanAddress? Current
    {
        get
        {
            lock (_gate)
            {
                if (_changed is not null)
                {
                    return _current;
                }
            }
            return Read();
        }
    }

    /// <summary>Raised on a thread of the pool when the chosen address became another one, or none.</summary>
    public event EventHandler? Changed
    {
        add
        {
            bool first;
            lock (_gate)
            {
                first = _changed is null;
                _changed += value;
                if (first)
                {
                    NetworkChange.NetworkAddressChanged += OnAddressChanged;
                }
            }
            if (first)
            {
                // Off the UI thread: listing the adapters can take a moment.
                _ = Task.Run(Refresh);
            }
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
                if (_changed is null)
                {
                    NetworkChange.NetworkAddressChanged -= OnAddressChanged;
                    _current = null;
                }
            }
        }
    }

    public void Dispose() => NetworkChange.NetworkAddressChanged -= OnAddressChanged;

    private void OnAddressChanged(object? sender, EventArgs e) => Refresh();

    // Reads the adapters and tells the followers when the chosen address differs from the one read last.
    private void Refresh()
    {
        try
        {
            int read = Interlocked.Increment(ref _reads);
            var found = Read();
            EventHandler? changed;
            lock (_gate)
            {
                if (_changed is null || read < _stored)
                {
                    return;
                }
                _stored = read;
                if (Equals(found, _current))
                {
                    return;
                }
                _current = found;
                changed = _changed;
            }
            changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("The PC's network address could not be followed ({Error})", RemoteLog.Failure(ex));
        }
    }

    private LanAddress? Read()
    {
        try
        {
            return LanAddressRule.Choose(Candidates());
        }
        catch (NetworkInformationException ex)
        {
            logger.LogWarning("Windows did not list the network adapters ({Error})", RemoteLog.Failure(ex));
            return null;
        }
    }

    private static List<LanCandidate> Candidates()
    {
        var candidates = new List<LanCandidate>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }
            IPInterfaceProperties properties;
            uint index;
            try
            {
                properties = adapter.GetIPProperties();
                index = (uint)properties.GetIPv4Properties().Index;
            }
            catch (NetworkInformationException)
            {
                // An adapter without IPv4.
                continue;
            }
            bool gateway = properties.GatewayAddresses.Any(route =>
                route.Address.AddressFamily == AddressFamily.InterNetwork && !route.Address.Equals(IPAddress.Any));
            candidates.AddRange(properties.UnicastAddresses.Select(unicast =>
                new LanCandidate(unicast.Address, index, IsUp: true, adapter.NetworkInterfaceType, gateway)));
        }
        return candidates;
    }
}
