using Granary.Core.Playback;
using Microsoft.Extensions.Logging;
using Windows.Networking.Connectivity;

namespace Granary.App.Services;

/// <summary>
/// The link Windows reaches the internet over: Wi-Fi, mobile broadband, or Ethernet by its adapter's type; metered when
/// Windows says the user pays for it (a fixed or variable plan, roaming, or over the data limit), as a metered connection
/// set in Settings does.
/// </summary>
public sealed class WindowsNetworkLink(ILogger<WindowsNetworkLink> logger) : INetworkLink
{
    // IANA ifType ethernetCsmacd.
    private const uint EthernetInterfaceType = 6;

    public NetworkLink Current
    {
        get
        {
            try
            {
                if (NetworkInformation.GetInternetConnectionProfile() is not { } profile)
                {
                    return NetworkLink.Unknown;
                }
                string transport = profile.IsWlanConnectionProfile ? NetworkTransports.Wifi
                    : profile.IsWwanConnectionProfile ? NetworkTransports.Cellular
                    : profile.NetworkAdapter?.IanaInterfaceType == EthernetInterfaceType ? NetworkTransports.Ethernet
                    : NetworkTransports.Other;
                var cost = profile.GetConnectionCost();
                bool metered = cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable || cost.Roaming || cost.OverDataLimit;
                return new NetworkLink(transport, metered);
            }
            catch (Exception ex)
            {
                logger.LogInformation("Windows did not describe the network link ({Failure})", ex.GetType().Name);
                return NetworkLink.Unknown;
            }
        }
    }
}
