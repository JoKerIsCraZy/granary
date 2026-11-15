using System.Net;
using System.Net.Sockets;

namespace Granary.Core.Remote;

/// <summary>
/// The receiver's decisions as pure functions, following silo-roku's <c>policy.bs</c> (its copy of silo-apple's
/// <c>SiloControlReceiverPolicy</c> with the Roku type's limits), without anything about borrowed identities.
/// </summary>
public static class RemoteReceiverPolicy
{
    /// <summary>Connections at a time: the Roku type's four, not the other TVs' eight.</summary>
    public const int MaxControllers = 4;

    /// <summary>New connections one address may open in a minute; more are closed unread.</summary>
    public const int MaxNewConnectionsPerMinute = 20;

    /// <summary>
    /// Connections at a time that have not said <c>hello</c>, so have proven nothing: naming the key's id proves nothing,
    /// since the id is public in the advertisement. A newcomer beyond them closes the oldest such, not itself.
    /// </summary>
    public const int MaxUnauthenticated = 4;

    /// <summary>Connections at a time from one address that have not said <c>hello</c>; a third closes that address's oldest.</summary>
    public const int MaxUnauthenticatedPerAddress = 2;

    /// <summary>
    /// Whether a peer may connect at all: a private (RFC 1918), link-local or loopback IPv4 address, also when written as
    /// an IPv4-mapped IPv6 one (<c>isLocalPeer</c>). Every other address is closed at once.
    /// </summary>
    public static bool IsLocalPeer(IPAddress peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var address = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        Span<byte> octets = stackalloc byte[4];
        address.TryWriteBytes(octets, out _);
        return octets[0] switch
        {
            10 or 127 => true,
            172 => octets[1] is >= 16 and <= 31,
            192 => octets[1] == 168,
            169 => octets[1] == 254,
            _ => false,
        };
    }

    /// <summary>
    /// What to do with a phone's <c>hello</c>, given the other connections it is weighed against: admit it below
    /// <see cref="MaxControllers"/>; at the limit close, oldest first, one that never said hello, then an unauthorized one,
    /// then a passive one; otherwise refuse the newcomer (<c>admission</c>). Granary weighs a hello only against phones
    /// that said theirs, so a peer that has proven nothing never takes an authenticated phone's place.
    /// </summary>
    public static Admission Admit(IReadOnlyList<ControllerFacts> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (existing.Count < MaxControllers)
        {
            return new Admission.Admit();
        }
        var byAge = existing.OrderBy(facts => facts.ConnectedAt).ThenBy(facts => facts.Number).ToList();
        var goes = byAge.FirstOrDefault(facts => !facts.SaidHello)
            ?? byAge.FirstOrDefault(facts => !facts.IsAuthorized)
            ?? byAge.FirstOrDefault(facts => facts.IsPassive);
        return goes is null ? new Admission.Refuse() : new Admission.Evict(goes.Number);
    }

    /// <summary>
    /// The other connections of the phone that said hello on <paramref name="number"/>: sockets it left behind, which the
    /// newest replaces (<c>replaced</c>). None for a phone without an id.
    /// </summary>
    public static IReadOnlyList<int> ReplacedBy(string deviceId, int number, IReadOnlyList<ControllerFacts> existing)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(existing);
        if (deviceId.Length == 0)
        {
            return [];
        }
        return [.. existing.Where(facts => facts.Number != number && string.Equals(facts.DeviceId, deviceId, StringComparison.Ordinal))
            .Select(facts => facts.Number)];
    }

    /// <summary>
    /// A <c>hello</c> as silo-roku's <c>receiver.bs</c> judges it: a phone's, listing version 2 and naming a server, or
    /// refused; authorized when its server is Granary's by id or by identity, else connected without status or control.
    /// </summary>
    public static HelloVerdict Judge(SiloCastMessage.PhoneHello hello, ReceiverIdentity receiver)
    {
        ArgumentNullException.ThrowIfNull(hello);
        ArgumentNullException.ThrowIfNull(receiver);
        if (hello.Role != SiloCastRoles.Phone || !hello.SupportedVersions.Contains(SiloCastMessages.Version)
            || string.IsNullOrEmpty(hello.ServerId))
        {
            return HelloVerdict.Refused;
        }
        return SiloCastServerId.ServersMatch(hello.ServerId, hello.ServerIdentity, receiver.ServerId, receiver.ServerIdentity)
            ? HelloVerdict.Authorized
            : HelloVerdict.Unauthorized;
    }
}

/// <summary>What the receiver does with a phone's <c>hello</c>.</summary>
public abstract record Admission
{
    private Admission()
    {
    }

    /// <summary>There is room.</summary>
    public sealed record Admit : Admission;

    /// <summary>Connection <paramref name="Number"/> is closed to make room.</summary>
    public sealed record Evict(int Number) : Admission;

    /// <summary>The newcomer is told there are too many and closed.</summary>
    public sealed record Refuse : Admission;
}

/// <summary>How a <c>hello</c> was judged.</summary>
public enum HelloVerdict
{
    /// <summary>A phone on Granary's server: status and control.</summary>
    Authorized,

    /// <summary>A phone on another server: connected, without status or control.</summary>
    Unauthorized,

    /// <summary>Not a phone, not version 2, or no server: <c>error version_unsupported</c> and closed.</summary>
    Refused,
}

/// <summary>What the policy needs to know of one connection.</summary>
/// <param name="Number">The connection's number.</param>
/// <param name="SaidHello">It said <c>hello</c>.</param>
/// <param name="IsAuthorized">Its phone is on Granary's server.</param>
/// <param name="IsPassive">Its phone only watches.</param>
/// <param name="ConnectedAt">When it was accepted, as a timestamp: the oldest goes first.</param>
/// <param name="DeviceId">The phone's own id, empty before its hello.</param>
public sealed record ControllerFacts(int Number, bool SaidHello, bool IsAuthorized, bool IsPassive, long ConnectedAt, string DeviceId = "")
{
    public override string ToString() => nameof(ControllerFacts);
}
