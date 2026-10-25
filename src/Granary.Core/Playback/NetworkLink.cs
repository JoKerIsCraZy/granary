namespace Granary.Core.Playback;

/// <summary>The transports a speed test result names (<c>transport</c> of the probe results).</summary>
public static class NetworkTransports
{
    public const string Wifi = "wifi";
    public const string Ethernet = "ethernet";
    public const string Cellular = "cellular";
    public const string Other = "other";
}

/// <summary>The link this device reaches the internet over: its <see cref="NetworkTransports"/> kind, and whether it is paid by the byte.</summary>
public sealed record NetworkLink(string Transport, bool IsMetered)
{
    /// <summary>A link the system cannot describe counts as unmetered, as a browser that cannot tell does.</summary>
    public static NetworkLink Unknown { get; } = new(NetworkTransports.Other, false);
}

/// <summary>The system's view of the network; the Windows one lives in the app.</summary>
public interface INetworkLink
{
    NetworkLink Current { get; }
}

/// <summary>Without the system's view: every link is <see cref="NetworkLink.Unknown"/>.</summary>
public sealed class UnknownNetworkLink : INetworkLink
{
    public NetworkLink Current => NetworkLink.Unknown;
}
