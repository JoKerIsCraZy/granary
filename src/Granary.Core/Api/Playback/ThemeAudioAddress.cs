using System.Net;

namespace Granary.Core.Api.Playback;

/// <summary>A theme's audio address as <see cref="PlaybackUrl.ResolveTheme"/> took it.</summary>
/// <param name="Node">
/// On a proxy node: what its connection may reach, checked again on the addresses its name resolves to. Null on the server's
/// own origin, which connects as it resolves, wherever the server runs, this PC included.
/// </param>
public sealed record ThemeAudioAddress(Uri Url, ThemeNode? Node);

/// <summary>A proxy node's connection rule: never this PC, a link-local, multicast or unspecified address, however written.</summary>
/// <param name="TakesPrivate">
/// A private, shared or unique-local address is taken too: the server authorizes media origins, or is on a private network
/// itself.
/// </param>
public sealed record ThemeNode(bool TakesPrivate)
{
    public bool Takes(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return !NetworkAddresses.IsNeverANode(address) && (TakesPrivate || !NetworkAddresses.IsPrivate(address));
    }
}
