namespace Granary.Core.Api.Admin;

/// <summary>
/// What invitations offer on this server (<c>GET /api/v2/admin/invitations/capabilities</c>): <c>state</c> is
/// <c>available</c> where they work at all.
/// </summary>
public sealed record AdminInvitationCapabilities
{
    public string State { get; init => field = value ?? ""; } = "";

    /// <summary>The server can make the invitee's first profile with the account.</summary>
    public bool DefaultProfile { get; init; }

    /// <summary>Email is set up, so an invitation can be emailed.</summary>
    public bool EmailDelivery { get; init; }

    public bool Available => State == "available";
}

/// <summary>
/// An invitation of the history (<c>GET /api/v2/admin/invitations</c>). Its address and note are personal data: shown,
/// never logged. A pending link invitation has no address until someone accepts it.
/// </summary>
public sealed record AdminInvitation
{
    public string Id { get; init => field = value ?? ""; } = "";

    public string Email { get; init => field = value ?? ""; } = "";

    /// <summary>link, email_sent, email_unconfirmed, or unknown (made before delivery was recorded).</summary>
    public string Delivery { get; init => field = value ?? ""; } = "";

    public string Role { get; init => field = value ?? ""; } = "";

    public string? AccessGroupId { get; init; }

    public IReadOnlyList<string>? LibraryIds { get; init; }

    public bool CreateProfile { get; init; }

    public bool ShowTour { get; init; }

    public string Note { get; init => field = value ?? ""; } = "";

    public string InvitedByName { get; init => field = value ?? ""; } = "";

    /// <summary>pending, accepted, revoked or expired.</summary>
    public string Status { get; init => field = value ?? ""; } = "";

    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>A link to share, bound to no address until it is accepted.</summary>
    public bool IsLink => Email.Length == 0;
}

public sealed record AdminInvitationCollection
{
    public IReadOnlyList<AdminInvitation> Items { get; init => field = value ?? []; } = [];

    public AdminPageInfo? Page { get; init; }
}

/// <summary>How an invitation reaches the invitee: a link to share, or an email.</summary>
public static class InvitationDeliveries
{
    public const string Link = "link";
    public const string Email = "email";
}

/// <summary>A new invitation (<c>POST /api/v2/admin/invitations</c>); left out, a part takes the server's default.</summary>
public sealed record AdminInvitationDraft
{
    /// <summary>Only for an emailed invitation; a link has none.</summary>
    public string? Email { get; init; }

    public required string Delivery { get; init; }

    public required string Role { get; init; }

    /// <summary>Left out for the default group.</summary>
    public string? AccessGroupId { get; init; }

    /// <summary>Left out to take the group's libraries.</summary>
    public IReadOnlyList<string>? LibraryIds { get; init; }

    public bool CreateProfile { get; init; }

    public bool ShowTour { get; init; }

    public string? Note { get; init; }
}

/// <summary>What a resend asks for: a new link only, or an email of it.</summary>
public sealed record AdminInvitationResend(string Delivery);

/// <summary>
/// An invitation made or made again, with its claim link. The link is a bearer credential, disclosed this once: it is shown
/// to the administrator who made it and copied on request, never logged nor kept; the record's text leaves it out.
/// </summary>
public sealed record InvitationDelivery
{
    public AdminInvitation Invitation { get; init => field = value ?? new(); } = new();

    public string ClaimUrl { get; init => field = value ?? ""; } = "";

    /// <summary>sent, not_configured, failed_or_unknown, or not_requested (a link: nothing was emailed).</summary>
    public string DeliveryStatus { get; init => field = value ?? ""; } = "";

    public override string ToString() => $"InvitationDelivery {{ Invitation = {Invitation.Id}, DeliveryStatus = {DeliveryStatus} }}";
}
