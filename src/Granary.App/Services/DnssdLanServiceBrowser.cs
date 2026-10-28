using Granary.Core.Servers;
using Microsoft.Extensions.Logging;
using Windows.Devices.Enumeration;

namespace Granary.App.Services;

/// <summary>
/// Browses <c>_silo._tcp</c> with Windows' own DNS-SD: a <see cref="DeviceWatcher"/> over association-endpoint services
/// of the DNS-SD protocol, which resolves each instance to its addresses, port and TXT strings and reports when they
/// change or go. Windows' mDNS querier asks from port 5353, which is all Silo's responder answers.
/// </summary>
public sealed class DnssdLanServiceBrowser(ILogger<DnssdLanServiceBrowser> logger) : ILanServiceBrowser
{
    private const string InstanceNameKey = "System.Devices.Dnssd.InstanceName";
    private const string AddressesKey = "System.Devices.IpAddress";
    private const string PortKey = "System.Devices.Dnssd.PortNumber";
    private const string TextKey = "System.Devices.Dnssd.TextAttributes";

    // Instances followed at once; discovery follows fewer still.
    private const int MaxInstances = 64;

    // The DNS-SD protocol of association endpoints, in local. only.
    private static readonly string Query =
        "System.Devices.AepService.ProtocolId:={4526e8c1-8aac-4153-9b16-55e86ada0e54} AND " +
        "System.Devices.Dnssd.Domain:=\"local\" AND " +
        $"System.Devices.Dnssd.ServiceName:=\"{LanAdverts.ServiceType}\"";

    private static readonly string[] Properties = [InstanceNameKey, AddressesKey, PortKey, TextKey];

    private readonly Lock _gate = new();
    private readonly Dictionary<string, DeviceInformation> _found = new(StringComparer.Ordinal);
    private DeviceWatcher? _watcher;

    public void Start(ILanBrowseEvents events)
    {
        ArgumentNullException.ThrowIfNull(events);
        DeviceWatcher watcher;
        lock (_gate)
        {
            StopLocked();
            try
            {
                watcher = DeviceInformation.CreateWatcher(Query, Properties, DeviceInformationKind.AssociationEndpointService);
            }
            catch (Exception ex)
            {
                logger.LogInformation("Windows could not browse the network ({Failure})", ex.GetType().Name);
                events.Failed();
                return;
            }
            _watcher = watcher;
        }
        // Each handler works for its own watcher only; the events go out of the lock, to whoever takes their own.
        watcher.Added += (_, info) =>
        {
            if (Keep(watcher, info))
            {
                events.Resolved(info.Id, Advert(info.Properties));
            }
        };
        watcher.Updated += (_, update) =>
        {
            if (Merge(watcher, update) is { } info)
            {
                events.Resolved(info.Id, Advert(info.Properties));
            }
        };
        watcher.Removed += (_, update) =>
        {
            if (Forget(watcher, update.Id))
            {
                events.Gone(update.Id);
            }
        };
        watcher.Stopped += (_, _) =>
        {
            if (IsCurrent(watcher) && watcher.Status == DeviceWatcherStatus.Aborted)
            {
                logger.LogInformation("Windows stopped browsing the network");
                events.Failed();
            }
        };
        try
        {
            watcher.Start();
        }
        catch (Exception ex)
        {
            logger.LogInformation("Windows could not browse the network ({Failure})", ex.GetType().Name);
            events.Failed();
            return;
        }
        // Stopped while it started: it must not browse on unseen.
        if (!IsCurrent(watcher))
        {
            try
            {
                watcher.Stop();
            }
            catch (Exception ex)
            {
                logger.LogDebug("Stopping the network browse failed ({Failure})", ex.GetType().Name);
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        var watcher = _watcher;
        _watcher = null;
        _found.Clear();
        if (watcher?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
        {
            try
            {
                watcher.Stop();
            }
            catch (Exception ex)
            {
                logger.LogDebug("Stopping the network browse failed ({Failure})", ex.GetType().Name);
            }
        }
    }

    private bool IsCurrent(DeviceWatcher watcher)
    {
        lock (_gate)
        {
            return ReferenceEquals(_watcher, watcher);
        }
    }

    // Anyone on the network can advertise: past the cap, a new instance is not followed until one goes.
    private bool Keep(DeviceWatcher watcher, DeviceInformation info)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_watcher, watcher) || (!_found.ContainsKey(info.Id) && _found.Count >= MaxInstances))
            {
                return false;
            }
            _found[info.Id] = info;
            return true;
        }
    }

    // An update names only what changed: the instance keeps the rest.
    private DeviceInformation? Merge(DeviceWatcher watcher, DeviceInformationUpdate update)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_watcher, watcher) || !_found.TryGetValue(update.Id, out var info))
            {
                return null;
            }
            info.Update(update);
            return info;
        }
    }

    private bool Forget(DeviceWatcher watcher, string id)
    {
        lock (_gate)
        {
            return ReferenceEquals(_watcher, watcher) && _found.Remove(id);
        }
    }

    // What Windows resolved, as it gives it: values of another kind than expected count as absent.
    private static LanAdvertisement Advert(IReadOnlyDictionary<string, object> properties) => new(
        properties.TryGetValue(InstanceNameKey, out var name) && name is string instance ? instance : "",
        properties.TryGetValue(AddressesKey, out var addresses) && addresses is string[] list ? list : [],
        properties.TryGetValue(PortKey, out var port) && port is ushort number ? number : 0,
        properties.TryGetValue(TextKey, out var text) && text is string[] records ? records : []);
}
