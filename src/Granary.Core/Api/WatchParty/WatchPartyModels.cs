using System.Text.Json.Serialization;

namespace Granary.Core.Api.WatchParty;

/// <summary>
/// <c>GET /api/v2/watch-together/capabilities</c>: whether rooms are served and which of their features. Clients decide
/// what to show from it, not from the server's version.
/// </summary>
public sealed record WatchPartyCapabilities
{
    /// <summary><c>available</c>, <c>disabled</c>, <c>not_configured</c> or <c>unsupported</c>.</summary>
    public string State { get; init => field = value ?? ""; } = "";

    /// <summary>The account may use rooms.</summary>
    public bool Allowed { get; init; }

    /// <summary><c>silo.room.v2</c> while the room socket is served; empty otherwise.</summary>
    public string SocketProtocol { get; init => field = value ?? ""; } = "";

    /// <summary>A lobby stages an item and the host starts it.</summary>
    public bool StagedSelection { get; init; }

    /// <summary>The host can stop playback and bring everyone back to the lobby.</summary>
    public bool StopPlayback { get; init; }

    /// <summary>Guests mark themselves ready in the lobby.</summary>
    public bool LobbyReady { get; init; }

    /// <summary>A displaced socket is told so before it closes.</summary>
    public bool ConnectionReplaced { get; init; }

    /// <summary>Rooms work here: served, allowed, and over the socket this client speaks.</summary>
    public bool IsAvailable => State == "available" && Allowed && SocketProtocol == WatchPartyApi.Protocol;
}

/// <summary>A room as every room operation answers it, with the renewed proof that this profile is in it.</summary>
/// <param name="RoomAccessToken">The room proof (<c>X-Room-Token</c>): never logged, never shown.</param>
public sealed record WatchPartyRoomResponse(WatchPartyRoom Room, string RoomAccessToken);

/// <summary>A room snapshot (silo-server <c>docs/architecture/watch-together-rooms.md</c>).</summary>
public sealed record WatchPartyRoom
{
    public string RoomId { get; init => field = value ?? ""; } = "";

    /// <summary>The eight-letter code guests join with.</summary>
    public string Code { get; init => field = value ?? ""; } = "";

    /// <summary><see cref="WatchPartyPhases"/>.</summary>
    public string Phase { get; init => field = value ?? ""; } = "";

    /// <summary><see cref="WatchPartyPlaybackStates"/>.</summary>
    public string PlaybackState { get; init => field = value ?? ""; } = "";

    /// <summary><c>host_pick</c> or <c>vote</c>.</summary>
    public string SelectionMode { get; init => field = value ?? ""; } = "";

    /// <summary><c>host_only</c> or <c>guest_play_pause</c>.</summary>
    public string GuestControlPolicy { get; init => field = value ?? ""; } = "";

    /// <summary><c>host</c> or <c>guest</c>.</summary>
    public string SelfRole { get; init => field = value ?? ""; } = "";

    /// <summary>The playback epoch: it advances when playback starts, restarts with other content, or stops.</summary>
    public long SelectionRevision { get; init; }

    /// <summary>Advances with every change; a snapshot with a lower one is older.</summary>
    public long Generation { get; init; }

    /// <summary>The room's position at <see cref="AnchorUpdatedAt"/>, advancing from there while it plays.</summary>
    public double AnchorPositionSeconds { get; init; }

    public DateTimeOffset AnchorUpdatedAt { get; init; }

    public bool IsPaused { get; init; }

    public bool HostConnected { get; init; }

    public int MemberCount { get; init; }

    /// <summary>Connected members across the server's nodes, host first.</summary>
    public IReadOnlyList<WatchPartyMember> Members { get; init => field = value ?? []; } = [];

    /// <summary>The staged or playing title; absent in an empty lobby.</summary>
    public string? SelectedContentId { get; init; }

    /// <summary>The one file every viewer plays (a number on the socket, a string over HTTP).</summary>
    [JsonConverter(typeof(FlexibleIdConverter))]
    public string? SelectedFileId { get; init; }

    /// <summary>The playback session this member attached, as the server has it.</summary>
    public string? AttachedSessionId { get; init; }

    /// <summary>The invitation's path on the web client, with its join token: as secret as the room proof.</summary>
    public string? InvitePath { get; init; }

    public bool SelfCanControlTransport { get; init; }

    public bool SelfCanManageRoom { get; init; }

    /// <summary>The room stopped waiting for this viewer, who catches up alone and acknowledges recovery.</summary>
    public bool SelfIgnoreWait { get; init; }

    public bool IsHost => SelfRole == "host";

    public bool IsPlaying => Phase == WatchPartyPhases.Playing;

    public bool IsLobby => Phase == WatchPartyPhases.Lobby;

    public bool IsEnded => Phase == WatchPartyPhases.Ended;

    /// <summary>A lobby with a title waiting for the host's start.</summary>
    public bool IsStaged => IsLobby && !string.IsNullOrEmpty(SelectedContentId);

    public bool GuestsCanPause => GuestControlPolicy == WatchPartyPolicies.GuestPlayPause;

    public WatchPartyMember? Self => Members.FirstOrDefault(m => m.IsSelf);

    public WatchPartyMember? Host => Members.FirstOrDefault(m => m.IsHost);

    /// <summary>This viewer's playback runs behind a room that went on without it (<c>self_ignore_wait</c>, or still buffering).</summary>
    public bool SelfCatchingUp => IsPlaying && PlaybackState is WatchPartyPlaybackStates.Playing or WatchPartyPlaybackStates.Paused
        && (SelfIgnoreWait || Self?.IsBuffering == true);
}

public sealed record WatchPartyMember
{
    /// <summary>A number on the socket, a string over HTTP.</summary>
    [JsonConverter(typeof(FlexibleIdConverter))]
    public string UserId { get; init => field = value ?? ""; } = "";

    public string ProfileId { get; init => field = value ?? ""; } = "";

    public string DisplayName { get; init => field = value ?? ""; } = "";

    public bool IsHost { get; init; }

    public bool IsSelf { get; init; }

    public bool Connected { get; init; }

    /// <summary>Marked ready in the lobby.</summary>
    public bool LobbyReady { get; init; }

    /// <summary>Acknowledged the room's waiting command.</summary>
    public bool IsReady { get; init; }

    public bool IsBuffering { get; init; }

    /// <summary>Attached and still holding the room's readiness barrier.</summary>
    public bool IsSyncing { get; init; }

    /// <summary>The member as suggestions and picker rows name it: <c>user:profile</c>.</summary>
    public string Key => UserId + ":" + ProfileId;
}

public static class WatchPartyPhases
{
    public const string Lobby = "lobby";
    public const string Playing = "playing";
    public const string Ended = "ended";
}

public static class WatchPartyPlaybackStates
{
    public const string Idle = "idle";

    /// <summary>The buffering barrier: the room holds until its members acknowledged the command.</summary>
    public const string Waiting = "waiting";

    public const string Paused = "paused";
    public const string Playing = "playing";
}

public static class WatchPartyPolicies
{
    public const string HostOnly = "host_only";
    public const string GuestPlayPause = "guest_play_pause";
}

/// <summary>The body of <c>POST /api/v2/watch-together/rooms</c>.</summary>
/// <param name="RoomId">A UUID the client picks: a retry with the same one answers the same room.</param>
/// <param name="SelectionMode"><c>host_pick</c> or <c>vote</c>.</param>
public sealed record WatchPartyCreate(string RoomId, string SelectionMode);

/// <summary>The body of <c>POST /api/v2/watch-together/join</c>: a code, or an invitation's token, which wins.</summary>
public sealed record WatchPartyJoin
{
    public string? Code { get; init; }

    public string? JoinToken { get; init; }
}

/// <summary>The body of <c>PUT …/staged-selection</c>: the title the lobby shows, not yet playing.</summary>
public sealed record WatchPartyStage(string ContentId);

/// <summary>The body of <c>PATCH …/policy</c>.</summary>
public sealed record WatchPartyPolicyChange(string GuestControlPolicy);

/// <summary>A ticket for one room socket connection; it expires within 30 seconds.</summary>
/// <param name="MaxConnectionSeconds">The server closes the connection after this (300).</param>
public sealed record WatchPartySocketTicket(string Ticket, int ExpiresIn, int MaxConnectionSeconds, string Protocol)
{
    /// <summary>A ticket of this protocol and of the web's shape (43 URL-safe characters), to travel in a header.</summary>
    public bool IsValid => Protocol == WatchPartyApi.Protocol && Ticket is { Length: 43 }
        && Ticket.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') && MaxConnectionSeconds is >= 1 and <= 3_600;
}

/// <summary>A command the room sent this member: what the player does at <see cref="ExecuteAt"/>, in the server's clock.</summary>
/// <param name="SessionId">A command for one member names its playback session; absent for everyone's.</param>
/// <param name="Action"><c>play</c>, <c>pause</c> or <c>seek</c>.</param>
/// <param name="PositionSeconds">The room's position at <see cref="ExecuteAt"/>.</param>
/// <param name="PlaybackState">The room's state once the command runs; <c>waiting</c> asks for a readiness acknowledgement.</param>
public sealed record WatchPartyCommand(string CommandId, string? SessionId, long SelectionRevision, string Action, double PositionSeconds,
    DateTimeOffset ExecuteAt, string PlaybackState)
{
    public const string Play = "play";
    public const string Pause = "pause";
    public const string Seek = "seek";
}
