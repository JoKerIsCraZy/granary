using System.Text.Json.Nodes;

namespace Granary.Core.Api.Admin;

/// <summary>
/// Invitations, as the web's Admin → Users → Invitations sends them (<c>api/v2/invitations.ts</c>, docs/api-contract.md,
/// 7.16b). A made or resent invitation answers its claim link once; nothing here retries a write.
/// </summary>
public sealed class AdminInvitationsApi(SiloHttp http)
{
    /// <summary>The invitations a page holds, as the web asks.</summary>
    public const int PageSize = 50;

    public Task<AdminInvitationCapabilities> GetCapabilitiesAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/admin/invitations/capabilities", SiloAuth.Profile, AdminJsonContext.Default.AdminInvitationCapabilities,
            cancellationToken);

    /// <summary>
    /// A page of the history, newest first. A page that says there is more without a new cursor, or less with one, fails the
    /// read, as the web's <c>listAdminInvitationsPage</c>.
    /// </summary>
    public async Task<AdminInvitationCollection> ListPageAsync(Uri server, string? cursor, CancellationToken cancellationToken = default)
    {
        string path = $"api/v2/admin/invitations?limit={PageSize}" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
        var page = await http.GetAsync(server, path, SiloAuth.Profile, AdminJsonContext.Default.AdminInvitationCollection, cancellationToken);
        bool continues = page.Page is { HasMore: true, NextCursor: { Length: > 0 } next } && next != cursor;
        bool ends = page.Page is { HasMore: false, NextCursor: null or "" };
        if (!continues && !ends)
        {
            throw new HttpRequestException(HttpRequestError.InvalidResponse, "The server's invitation pages do not continue.");
        }
        return page;
    }

    /// <summary>
    /// Whether the server lacks its public URL, which every invitation link is built on, as the web's Invitations tab reads
    /// <c>server.public_url</c> of the administrator settings. Only that value is taken from the answer; nothing of it is
    /// kept or logged.
    /// </summary>
    public async Task<bool> IsPublicUrlMissingAsync(Uri server, CancellationToken cancellationToken = default)
    {
        var settings = await http.GetAsync(server, "api/v2/admin/settings/effective", SiloAuth.Profile, AdminJsonContext.Default.JsonObject,
            cancellationToken);
        return settings["server.public_url"] is not JsonValue url || !url.TryGetValue(out string? text) || string.IsNullOrWhiteSpace(text);
    }

    public async Task<InvitationDelivery> CreateAsync(Uri server, AdminInvitationDraft draft, CancellationToken cancellationToken = default) =>
        Checked(await http.PostAsync(server, "api/v2/admin/invitations", SiloAuth.Profile, draft, AdminJsonContext.Default.AdminInvitationDraft,
            AdminJsonContext.Default.InvitationDelivery, cancellationToken));

    /// <summary>
    /// A new link for a pending or expired invitation; the old one stops working. <see cref="InvitationDeliveries.Link"/>
    /// emails nothing and keeps an emailed invitation's address; <see cref="InvitationDeliveries.Email"/> emails it.
    /// </summary>
    public async Task<InvitationDelivery> ResendAsync(Uri server, string id, string delivery, CancellationToken cancellationToken = default) =>
        Checked(await http.PostAsync(server, InvitationPath(id) + "/resend", SiloAuth.Profile, new AdminInvitationResend(delivery),
            AdminJsonContext.Default.AdminInvitationResend, AdminJsonContext.Default.InvitationDelivery, cancellationToken));

    /// <summary>Revokes a pending invitation: its link stops working.</summary>
    public Task RevokeAsync(Uri server, string id, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Delete, server, InvitationPath(id), SiloAuth.Profile, cancellationToken);

    private static string InvitationPath(string id) => $"api/v2/admin/invitations/{SiloHttp.PathSegment(id, nameof(id))}";

    // The web's delivery(): an answer without its invitation, link or outcome may still have made one.
    private static InvitationDelivery Checked(InvitationDelivery result) =>
        result.Invitation.Id.Length > 0 && result.ClaimUrl.Length > 0
        && result.DeliveryStatus is "sent" or "not_configured" or "failed_or_unknown" or "not_requested"
            ? result
            : throw new HttpRequestException(HttpRequestError.InvalidResponse, "The invitation's answer is incomplete.");
}
