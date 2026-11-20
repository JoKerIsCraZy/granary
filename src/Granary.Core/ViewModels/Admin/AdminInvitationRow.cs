using Granary.Core.Api.Admin;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// One invitation of the history, as the web's <c>InvitationList</c> row: who it is for, its details, status and delivery,
/// and the actions its status allows: a new link (and an emailed one) while pending or expired, Revoke while pending.
/// Only the Owner resends an admin's invitation, as resending grants the role again. A new link needs the server's public
/// URL; revoking does not.
/// </summary>
public sealed record AdminInvitationRow(
    AdminInvitation Invitation,
    string Recipient,
    string Details,
    string RoleText,
    string StatusText,
    bool IsPending,
    string ExpiresText,
    string DeliveryText,
    string DeliveryGlyph,
    bool DeliveryUnconfirmed,
    string DeliveryTip,
    string CreatedText,
    bool CanResendLink,
    string ResendLinkText,
    bool CanResendEmail,
    bool CanRevoke,
    bool ResendEnabled)
{
    public string Id => Invitation.Id;

    public static AdminInvitationRow From(AdminInvitation invitation, bool viewerIsOwner, bool emailDelivery, bool publicUrlMissing = false)
    {
        bool link = invitation.IsLink;
        // A link invitation's note is how the administrator told it apart, so it stays after acceptance fills in the address.
        bool shared = invitation.Delivery == InvitationDeliveries.Link;
        List<string> details = [];
        if (!link && invitation.Status == "accepted" && shared)
        {
            details.Add(AppText.Get("web.admin.settings.invitations.row.joinedByLink"));
        }
        if (shared && invitation.Note.Length > 0)
        {
            details.Add(AppText.Get("web.admin.settings.invitations.row.note", ("note", invitation.Note)));
        }
        if (invitation.InvitedByName.Length > 0)
        {
            details.Add(AppText.Get("web.admin.settings.invitations.row.invitedBy", ("name", invitation.InvitedByName)));
        }
        bool resendable = (invitation.Role != AdminRoles.Admin || viewerIsOwner) && invitation.Status is "pending" or "expired";
        var (deliveryText, glyph, tip) = invitation.Delivery switch
        {
            "link" => (AppText.Get("web.admin.settings.invitations.deliveryLabel.link"), "", ""),
            "email_sent" => (AppText.Get("web.admin.settings.invitations.deliveryLabel.emailSent"), "", ""),
            "email_unconfirmed" => (AppText.Get("web.admin.settings.invitations.deliveryLabel.emailUnconfirmed"), "",
                AppText.Get("web.admin.settings.invitations.deliveryLabel.emailUnconfirmedTitle")),
            _ => ("—", "", AppText.Get("web.admin.settings.invitations.deliveryLabel.unknownTitle")),
        };
        return new AdminInvitationRow(
            invitation,
            link ? AppText.Get("web.admin.settings.invitations.row.linkInvitation") : invitation.Email,
            string.Join(" · ", details),
            // The web shows the role capitalised by its CSS.
            AppText.Get(invitation.Role == AdminRoles.Admin
                ? "web.admin.settings.invitations.create.roleAdmin"
                : "web.admin.settings.invitations.create.roleUser"),
            AppText.Get(invitation.Status switch
            {
                "accepted" => "web.admin.settings.invitations.status.accepted",
                "expired" => "web.admin.settings.invitations.status.expired",
                "revoked" => "web.admin.settings.invitations.status.revoked",
                _ => "web.admin.settings.invitations.status.pending",
            }),
            invitation.Status == "pending",
            invitation.Status == "pending"
                ? AppText.Get("web.admin.settings.invitations.row.expires", ("date", DateText.Date(invitation.ExpiresAt.LocalDateTime)))
                : "",
            deliveryText,
            glyph,
            invitation.Delivery == "email_unconfirmed",
            tip,
            DateText.Date(invitation.CreatedAt.LocalDateTime),
            resendable,
            AppText.Get(link ? "web.admin.settings.invitations.row.replaceLink" : "web.admin.settings.invitations.row.copyNewLink"),
            resendable && !link && emailDelivery,
            invitation.Status == "pending",
            !publicUrlMissing);
    }

    /// <summary>The web's deliveryMessage: what a made or resent invitation's delivery did.</summary>
    public static string DeliveryMessage(InvitationDelivery result) => result.DeliveryStatus switch
    {
        // A replaced link for an emailed invitation stays bound to its address.
        "not_requested" when result.Invitation.Email.Length > 0 =>
            AppText.Get("web.admin.settings.invitations.delivery.newLinkForEmail", ("email", result.Invitation.Email)),
        "not_requested" => AppText.Get("web.admin.settings.invitations.delivery.linkCreated"),
        "sent" => AppText.Get("web.admin.settings.invitations.delivery.sent", ("email", result.Invitation.Email)),
        "not_configured" => AppText.Get("web.admin.settings.invitations.delivery.notConfigured"),
        _ => AppText.Get("web.admin.settings.invitations.delivery.failed"),
    };
}
