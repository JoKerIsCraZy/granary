using System.Text.Json.Serialization;

namespace Granary.Core.Api.WatchParty;

/// <summary>
/// Watch Party rooms (silo-server <c>docs/realtime-api.md</c>, "Watch Together"): made, joined by code or invitation,
/// read, staged, started, stopped and ended over HTTP, and followed over the room socket. Reads and the socket ticket
/// carry the room proof (<c>X-Room-Token</c>) every room answer renews; the host's operations go by its profile alone.
/// Every write is sent once: an uncertain one is read back, never repeated.
/// </summary>
public sealed class WatchPartyApi(SiloHttp http)
{
    /// <summary>The room socket's protocol, offered first; the ticket goes second as <c>silo.ticket.&lt;ticket&gt;</c>.</summary>
    public const string Protocol = "silo.room.v2";

    private const string RoomTokenHeader = "X-Room-Token";

    public Task<WatchPartyCapabilities> GetCapabilitiesAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/watch-together/capabilities", SiloAuth.Profile, WatchPartyJsonContext.Default.WatchPartyCapabilities,
            cancellationToken);

    /// <summary>Opens a room this profile hosts, in which the host picks.</summary>
    /// <param name="roomId">A fresh UUID; retrying with it after a lost answer answers the same room.</param>
    public Task<WatchPartyRoomResponse> CreateAsync(Uri server, string roomId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);
        return http.PostAsync(server, "api/v2/watch-together/rooms", SiloAuth.Profile, new WatchPartyCreate(roomId, "host_pick"),
            WatchPartyJsonContext.Default.WatchPartyCreate, WatchPartyJsonContext.Default.WatchPartyRoomResponse, cancellationToken);
    }

    /// <summary>Resolves a code or an invitation's token to the room and this profile's proof for it.</summary>
    public Task<WatchPartyRoomResponse> JoinAsync(Uri server, WatchPartyJoin join, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(join);
        return http.PostAsync(server, "api/v2/watch-together/join", SiloAuth.Profile, join, WatchPartyJsonContext.Default.WatchPartyJoin,
            WatchPartyJsonContext.Default.WatchPartyRoomResponse, cancellationToken);
    }

    /// <summary>The room as it is now, and a renewed proof. 409 once it ended, 403 for a proof of another room or profile.</summary>
    public Task<WatchPartyRoomResponse> GetAsync(Uri server, string roomId, string roomToken, CancellationToken cancellationToken = default) =>
        http.SendWithHeadersAsync(HttpMethod.Get, server, RoomPath(roomId), SiloAuth.Profile, Proof(roomToken),
            WatchPartyJsonContext.Default.WatchPartyRoomResponse, cancellationToken);

    /// <summary>The host puts <paramref name="contentId"/> up in the lobby; nothing plays until <see cref="StartAsync"/>.</summary>
    public Task<WatchPartyRoomResponse> StageAsync(Uri server, string roomId, string contentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);
        return http.SendAsync(HttpMethod.Put, server, RoomPath(roomId) + "/staged-selection", SiloAuth.Profile, new WatchPartyStage(contentId),
            WatchPartyJsonContext.Default.WatchPartyStage, WatchPartyJsonContext.Default.WatchPartyRoomResponse, cancellationToken);
    }

    /// <summary>The host starts what the lobby staged, for everyone.</summary>
    public Task<WatchPartyRoomResponse> StartAsync(Uri server, string roomId, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Post, server, RoomPath(roomId) + "/playback/start", SiloAuth.Profile,
            WatchPartyJsonContext.Default.WatchPartyRoomResponse, cancellationToken);

    /// <summary>The host stops playback for everyone; the room goes back to its lobby, the title still staged.</summary>
    public Task<WatchPartyRoomResponse> StopAsync(Uri server, string roomId, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Post, server, RoomPath(roomId) + "/playback/stop", SiloAuth.Profile,
            WatchPartyJsonContext.Default.WatchPartyRoomResponse, cancellationToken);

    /// <summary>Whether guests may play and pause (the host only seeks).</summary>
    public Task<WatchPartyRoomResponse> SetPolicyAsync(Uri server, string roomId, bool guestsCanPause, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Patch, server, RoomPath(roomId) + "/policy", SiloAuth.Profile,
            new WatchPartyPolicyChange(guestsCanPause ? WatchPartyPolicies.GuestPlayPause : WatchPartyPolicies.HostOnly),
            WatchPartyJsonContext.Default.WatchPartyPolicyChange, WatchPartyJsonContext.Default.WatchPartyRoomResponse, cancellationToken);

    /// <summary>The host ends the room for everyone.</summary>
    public Task EndAsync(Uri server, string roomId, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Delete, server, RoomPath(roomId), SiloAuth.Profile, cancellationToken);

    /// <summary>A ticket for one connection of the room socket; it needs the room proof.</summary>
    public Task<WatchPartySocketTicket> MintTicketAsync(Uri server, string roomId, string roomToken, CancellationToken cancellationToken = default) =>
        http.SendWithHeadersAsync(HttpMethod.Post, server, RoomPath(roomId) + "/ws-ticket", SiloAuth.Profile, Proof(roomToken),
            WatchPartyJsonContext.Default.WatchPartySocketTicket, cancellationToken);

    /// <summary>The room socket's address on <paramref name="server"/>; no credential belongs in it.</summary>
    public static Uri SocketUri(Uri server, string roomId)
    {
        ArgumentNullException.ThrowIfNull(server);
        var builder = new UriBuilder(new Uri(server.AbsoluteUri.TrimEnd('/') + "/"))
        {
            Scheme = server.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Query = "",
            Fragment = "",
            UserName = "",
            Password = "",
        };
        builder.Path = builder.Path.TrimEnd('/') + "/" + RoomPath(roomId) + "/ws";
        if (builder.Uri.IsDefaultPort)
        {
            builder.Port = -1;
        }
        return builder.Uri;
    }

    /// <summary>The invitation as a link to the server's web client, from the room's <c>invite_path</c>.</summary>
    public static Uri? InviteLink(Uri server, WatchPartyRoom room)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(room);
        if (room.InvitePath is not { Length: > 1 } path || !path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }
        return Uri.TryCreate(server.AbsoluteUri.TrimEnd('/') + path, UriKind.Absolute, out var link) ? link : null;
    }

    private static string RoomPath(string roomId) => "api/v2/watch-together/rooms/" + SiloHttp.PathSegment(roomId, nameof(roomId));

    private static Dictionary<string, string> Proof(string roomToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomToken);
        return new(StringComparer.OrdinalIgnoreCase) { [RoomTokenHeader] = roomToken };
    }
}

/// <summary>The wire format of the Watch Party models: snake_case, nulls left out on write.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(WatchPartyCapabilities))]
[JsonSerializable(typeof(WatchPartyRoomResponse))]
[JsonSerializable(typeof(WatchPartyRoom))]
[JsonSerializable(typeof(WatchPartyCommand))]
[JsonSerializable(typeof(WatchPartyCreate))]
[JsonSerializable(typeof(WatchPartyJoin))]
[JsonSerializable(typeof(WatchPartyStage))]
[JsonSerializable(typeof(WatchPartyPolicyChange))]
[JsonSerializable(typeof(WatchPartySocketTicket))]
internal sealed partial class WatchPartyJsonContext : JsonSerializerContext;
