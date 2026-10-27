namespace Granary.Core.Api.Admin;

/// <summary>
/// The body of <c>POST /api/v2/admin/sessions/{id}/pause</c>, <c>resume</c>, <c>stop</c> and <c>message</c>: a command
/// the server applies once per session, in <see cref="Sequence"/> order; a retry keeps the same identity.
/// </summary>
/// <param name="CommandId">A UUID the client allocates for this one command.</param>
/// <param name="Message">The text a <c>message</c> command shows on the player; null for the other commands.</param>
public sealed record AdminPlaybackCommand(string CommandId, long Sequence, string? Message = null);

/// <param name="Delivery">dispatched, or fallback_scheduled when the player has no realtime lane (the server ends the session after a deadline, stop only).</param>
/// <param name="Outcome">applied, or replayed for a retry of a command already applied.</param>
public sealed record AdminPlaybackCommandReceipt(string CommandId, string Delivery, string Outcome, long Sequence);

/// <summary>The body of <c>POST /api/v2/admin/sessions/{id}/terminate</c>.</summary>
public sealed record AdminPlaybackTerminate(string? Reason = null);

/// <param name="AuthorityRevoked">The session's playback authority is revoked: its media and progress are refused from now on.</param>
/// <param name="AlreadyRevoked">The session was terminated before; nothing was sent again.</param>
/// <param name="ClientNotified">The player was told to stop; false when it could not be reached.</param>
public sealed record AdminPlaybackTerminateReceipt(string SessionId, bool AuthorityRevoked, bool AlreadyRevoked, bool ClientNotified, string Delivery);

/// <summary>An account seen at an address, from <c>GET /api/v2/admin/ips</c>.</summary>
public sealed record AdminIpUser(string UserId, string Username, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, long RequestCount);

public sealed record AdminIpUserCollection
{
    public IReadOnlyList<AdminIpUser> Items { get; init => field = value ?? []; } = [];

    public AdminPageInfo? Page { get; init; }
}

/// <summary>Cursor state of a page; <see cref="NextCursor"/> asks for the next one.</summary>
public sealed record AdminPageInfo(bool HasMore, string? NextCursor);
