using System.Text.Json.Serialization;
using Granary.Core.Api.Catalog;

namespace Granary.Core.Api.WatchParty;

/// <summary>
/// Watch Party rooms (silo-server <c>docs/realtime-api.md</c>, "Watch Together"): made, joined by code or invitation,
/// read, staged, started, stopped and ended over HTTP, and followed over the room socket; its suggestions and votes, and
/// the picker's rows. Reads, the socket ticket and everything a member does with suggestions carry the room proof
/// (<c>X-Room-Token</c>) every room answer renews; the host's room operations go by its profile alone. Every write is sent
/// once: an uncertain one is read back, never repeated.
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

    /// <summary>The host switches a lobby between host picks and voting; the staged title and lobby readiness go.</summary>
    public Task<WatchPartyRoomResponse> SetSelectionModeAsync(Uri server, string roomId, string mode, CancellationToken cancellationToken = default)
    {
        if (mode is not (WatchPartySelectionModes.HostPick or WatchPartySelectionModes.Vote))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "A room picks by host_pick or vote.");
        }
        return http.SendAsync(HttpMethod.Patch, server, RoomPath(roomId) + "/selection-mode", SiloAuth.Profile, new WatchPartySelectionModeChange(mode),
            WatchPartyJsonContext.Default.WatchPartySelectionModeChange, WatchPartyJsonContext.Default.WatchPartyRoomResponse, cancellationToken);
    }

    /// <summary>Every suggestion of the room, ranked as the server ranks them: most votes first, then the oldest.</summary>
    public async Task<IReadOnlyList<WatchPartySuggestion>> ListSuggestionsAsync(Uri server, string roomId, string roomToken,
        CancellationToken cancellationToken = default)
    {
        var suggestions = new List<WatchPartySuggestion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (int page = 0; page < MaxSuggestionPages; page++)
        {
            string path = RoomPath(roomId) + "/suggestions?limit=" + SuggestionPageSize
                + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var answer = await http.SendWithHeadersAsync(HttpMethod.Get, server, path, SiloAuth.Profile, Proof(roomToken),
                WatchPartyJsonContext.Default.WatchPartySuggestionPage, cancellationToken).ConfigureAwait(false);
            suggestions.AddRange(answer.Items.Where(s => s.Id.Length > 0 && seen.Add(s.Id)));
            if (answer.Page is not { HasMore: true, NextCursor: { Length: > 0 } next } || !cursors.Add(next))
            {
                return Rank(suggestions);
            }
            cursor = next;
        }
        return Rank(suggestions);
    }

    /// <summary>Puts a movie or an episode forward; the answer names the suggestion, the list comes from <see cref="ListSuggestionsAsync"/>.</summary>
    public Task<WatchPartySuggestionReceipt> SuggestAsync(Uri server, string roomId, string roomToken, WatchPartySuggestionCreate suggestion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        return http.SendWithHeadersAsync(HttpMethod.Post, server, RoomPath(roomId) + "/suggestions", SiloAuth.Profile, Proof(roomToken), suggestion,
            WatchPartyJsonContext.Default.WatchPartySuggestionCreate, WatchPartyJsonContext.Default.WatchPartySuggestionReceipt, cancellationToken);
    }

    /// <summary>This profile's vote for a suggestion, on or off; either state already set is no error.</summary>
    public Task SetVoteAsync(Uri server, string roomId, string roomToken, string suggestionId, bool vote, CancellationToken cancellationToken = default) =>
        http.SendWithHeadersAsync(vote ? HttpMethod.Post : HttpMethod.Delete, server, SuggestionPath(roomId, suggestionId) + "/vote", SiloAuth.Profile,
            Proof(roomToken), cancellationToken);

    /// <summary>Takes a suggestion back: its suggester's, or any as the host.</summary>
    public Task DeleteSuggestionAsync(Uri server, string roomId, string roomToken, string suggestionId, CancellationToken cancellationToken = default) =>
        http.SendWithHeadersAsync(HttpMethod.Delete, server, SuggestionPath(roomId, suggestionId), SiloAuth.Profile, Proof(roomToken), cancellationToken);

    /// <summary>The host plays a suggestion for everyone (any of them in a vote room, not only the leader).</summary>
    public Task<WatchPartyRoomResponse> PromoteAsync(Uri server, string roomId, string roomToken, string suggestionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestionId);
        return http.SendWithHeadersAsync(HttpMethod.Post, server, RoomPath(roomId) + "/suggestions/promote", SiloAuth.Profile, Proof(roomToken),
            new WatchPartyPromote(suggestionId), WatchPartyJsonContext.Default.WatchPartyPromote, WatchPartyJsonContext.Default.WatchPartyRoomResponse,
            cancellationToken);
    }

    /// <summary>The picker's rows over the room's connected members: what they watch together, and their watchlists.</summary>
    public Task<WatchPartyPicker> GetPickerAsync(Uri server, string roomId, string roomToken, CancellationToken cancellationToken = default) =>
        http.SendWithHeadersAsync(HttpMethod.Get, server, RoomPath(roomId) + "/picker", SiloAuth.Profile, Proof(roomToken),
            WatchPartyJsonContext.Default.WatchPartyPicker, cancellationToken);

    /// <summary>What each connected member has watched of <paramref name="contentIds"/> (at most 200), and whose watchlist has them.</summary>
    public Task<WatchPartyMemberStates> GetMemberStatesAsync(Uri server, string roomId, string roomToken, IReadOnlyList<string> contentIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contentIds);
        if (contentIds.Count is 0 or > MaxMemberStateIds)
        {
            throw new ArgumentOutOfRangeException(nameof(contentIds), contentIds.Count, "Ask for 1 to 200 titles at a time.");
        }
        return http.SendWithHeadersAsync(HttpMethod.Post, server, RoomPath(roomId) + "/member-state", SiloAuth.Profile, Proof(roomToken),
            new WatchPartyMemberStateQuery(contentIds), WatchPartyJsonContext.Default.WatchPartyMemberStateQuery,
            WatchPartyJsonContext.Default.WatchPartyMemberStates, cancellationToken);
    }

    /// <summary>Most votes first, then the oldest, then by id: the server's order.</summary>
    public static IReadOnlyList<WatchPartySuggestion> Rank(IEnumerable<WatchPartySuggestion> suggestions) =>
        [.. suggestions.OrderByDescending(s => s.VoteCount).ThenBy(s => s.CreatedAt).ThenBy(s => s.Id, StringComparer.Ordinal)];

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

    private const int SuggestionPageSize = 100;
    private const int MaxSuggestionPages = 100;

    /// <summary>The most titles one member-state read takes.</summary>
    public const int MaxMemberStateIds = 200;

    private static string RoomPath(string roomId) => "api/v2/watch-together/rooms/" + SiloHttp.PathSegment(roomId, nameof(roomId));

    private static string SuggestionPath(string roomId, string suggestionId) =>
        RoomPath(roomId) + "/suggestions/" + SiloHttp.PathSegment(suggestionId, nameof(suggestionId));

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
[JsonSerializable(typeof(WatchPartySelectionModeChange))]
[JsonSerializable(typeof(WatchPartySuggestion))]
[JsonSerializable(typeof(IReadOnlyList<WatchPartySuggestion>))]
[JsonSerializable(typeof(WatchPartySuggestionPage))]
[JsonSerializable(typeof(WatchPartySuggestionCreate))]
[JsonSerializable(typeof(WatchPartySuggestionReceipt))]
[JsonSerializable(typeof(WatchPartyPromote))]
[JsonSerializable(typeof(WatchPartyPicker))]
[JsonSerializable(typeof(WatchPartyMemberStateQuery))]
[JsonSerializable(typeof(WatchPartyMemberStates))]
[JsonSerializable(typeof(CatalogItem))]
[JsonSerializable(typeof(List<Granary.Core.WatchParty.WatchPartyRecentRoom>))]
internal sealed partial class WatchPartyJsonContext : JsonSerializerContext;
