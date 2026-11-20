namespace Granary.Core.Api.Admin;

/// <summary>
/// Signup invite codes, as the web's Admin → Users → Invite Codes reads and changes them (<c>hooks/queries/admin/inviteCodes.ts</c>,
/// docs/api-contract.md, 7.16c). Every call goes only as the account and profile the tab was opened as
/// (<see cref="AdminActor"/>). Nothing here retries a write: a top-up that fails may still have counted, so the list is read
/// again instead.
/// </summary>
public sealed class AdminInviteCodesApi(SiloHttp http)
{
    /// <summary>The codes a page holds, as the web asks.</summary>
    public const int PageSize = 50;

    private const string Path = "api/v2/admin/invite-codes";

    /// <summary>A page of the codes, newest first. A page that says there is more without a new cursor fails the read.</summary>
    public async Task<AdminInviteCodeCollection> ListPageAsync(AdminActor actor, string? cursor, CancellationToken cancellationToken = default)
    {
        string path = $"{Path}?limit={PageSize}" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
        var page = await http.GetAsAccountProfileAsync(actor.Server, path, actor.AccountId, actor.ProfileId,
            AdminJsonContext.Default.AdminInviteCodeCollection, cancellationToken);
        if (page.Page is { HasMore: true } more && (string.IsNullOrEmpty(more.NextCursor) || more.NextCursor == cursor))
        {
            throw new HttpRequestException(HttpRequestError.InvalidResponse, "The server's invite code pages do not continue.");
        }
        return page;
    }

    /// <summary>Makes a code; 409 when it exists with other settings.</summary>
    public Task<AdminInviteCode> CreateAsync(AdminActor actor, AdminInviteCodeDraft draft, CancellationToken cancellationToken = default) =>
        http.SendAsAccountProfileAsync(HttpMethod.Post, actor.Server, Path, actor.AccountId, actor.ProfileId, draft,
            AdminJsonContext.Default.AdminInviteCodeDraft, AdminJsonContext.Default.AdminInviteCode, cancellationToken);

    /// <summary>Turns a code on or off.</summary>
    public Task SetEnabledAsync(AdminActor actor, string id, bool enabled, CancellationToken cancellationToken = default) =>
        http.SendAsAccountProfileAsync(HttpMethod.Put, actor.Server, CodePath(id), actor.AccountId, actor.ProfileId, new AdminInviteCodeChange(enabled),
            AdminJsonContext.Default.AdminInviteCodeChange, cancellationToken);

    /// <summary>Adds uses to a code.</summary>
    public Task<AdminInviteCode> TopUpAsync(AdminActor actor, string id, int additionalUses, CancellationToken cancellationToken = default) =>
        http.SendAsAccountProfileAsync(HttpMethod.Post, actor.Server, CodePath(id) + "/top-up", actor.AccountId, actor.ProfileId,
            new AdminInviteCodeTopUp(additionalUses), AdminJsonContext.Default.AdminInviteCodeTopUp, AdminJsonContext.Default.AdminInviteCode,
            cancellationToken);

    /// <summary>Deletes a code: it no longer signs anyone up.</summary>
    public Task DeleteAsync(AdminActor actor, string id, CancellationToken cancellationToken = default) =>
        http.SendAsAccountProfileAsync(HttpMethod.Delete, actor.Server, CodePath(id), actor.AccountId, actor.ProfileId, cancellationToken);

    private static string CodePath(string id) => $"{Path}/{SiloHttp.PathSegment(id, nameof(id))}";
}
