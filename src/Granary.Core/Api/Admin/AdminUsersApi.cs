using System.Text.Json.Serialization.Metadata;
using Granary.Core.Api.Accounts;

namespace Granary.Core.Api.Admin;

/// <summary>
/// The server's login accounts, as the web's Admin → Users reads and changes them (<c>api/v2/adminUsers.ts</c>,
/// docs/api-contract.md, 7.16a). Every call needs an administrator account and goes only as the account and profile the
/// page was opened as (<see cref="AdminActor"/>). A change or delete carries the version the account was read at
/// (<c>If-Match</c>): 412 means another administrator changed it since.
/// </summary>
public sealed class AdminUsersApi(SiloHttp http)
{
    /// <summary>The most a page of accounts or groups holds, as the web asks.</summary>
    public const int PageSize = 200;

    private const int MaxPages = 500;

    /// <summary>
    /// Every account, every page of them as the web reads them (<c>listAdminUsers</c>): a page that does not continue, or a
    /// cursor that repeats, fails the read rather than show part of the list.
    /// </summary>
    public Task<IReadOnlyList<AdminUser>> ListAllAsync(AdminActor actor, CancellationToken cancellationToken = default) =>
        WalkAsync(actor, "api/v2/admin/users", AdminJsonContext.Default.AdminUserCollection, page => (page.Items, page.Page), cancellationToken);

    /// <summary>Every access group, for their names and the group new accounts join.</summary>
    public Task<IReadOnlyList<AdminAccessGroup>> ListAccessGroupsAsync(AdminActor actor, CancellationToken cancellationToken = default) =>
        WalkAsync(actor, "api/v2/admin/access-groups", AdminJsonContext.Default.AdminAccessGroupCollection, page => (page.Items, page.Page),
            cancellationToken);

    public Task<AdminUserCapabilities> GetCapabilitiesAsync(AdminActor actor, CancellationToken cancellationToken = default) =>
        http.GetAsAccountProfileAsync(actor.Server, "api/v2/admin/users/capabilities", actor.AccountId, actor.ProfileId,
            AdminJsonContext.Default.AdminUserCapabilities, cancellationToken);

    /// <summary>The server's built-in policy layers, which an admin's or an ungrouped account's unset fields resolve to.</summary>
    public Task<AdminPolicyDefaults> GetPolicyDefaultsAsync(AdminActor actor, CancellationToken cancellationToken = default) =>
        http.GetAsAccountProfileAsync(actor.Server, "api/v2/admin/users/policy-defaults", actor.AccountId, actor.ProfileId,
            AdminJsonContext.Default.AdminPolicyDefaults, cancellationToken);

    /// <summary>
    /// Public signups and the two download switches, from the administrator settings as the web's Users pages read them
    /// (<c>useAdminServerSettings</c>, values the text "true"). Only these three are taken from the answer; nothing of it is
    /// kept or logged.
    /// </summary>
    public async Task<AdminServerSwitches> GetServerSwitchesAsync(AdminActor actor, CancellationToken cancellationToken = default)
    {
        var settings = await http.GetAsAccountProfileAsync(actor.Server, "api/v2/admin/settings/effective", actor.AccountId, actor.ProfileId,
            AdminJsonContext.Default.JsonObject, cancellationToken);
        return new AdminServerSwitches(IsOn(settings, "signup.enabled"), IsOn(settings, "download.enabled"),
            IsOn(settings, "download.transcode_enabled"));
    }

    private static bool IsOn(System.Text.Json.Nodes.JsonObject settings, string key) =>
        settings[key] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue(out string? text) && text == "true";

    /// <summary>One account and the version a change or delete must name.</summary>
    public Task<Versioned<AdminUser>> GetAsync(AdminActor actor, string id, CancellationToken cancellationToken = default) =>
        http.GetVersionedAsAccountProfileAsync(actor.Server, UserPath(id), actor.AccountId, actor.ProfileId, AdminJsonContext.Default.AdminUser,
            cancellationToken);

    /// <summary>Creates the account; 409 when its username or email is taken.</summary>
    public Task<AdminUserCreated> CreateAsync(AdminActor actor, AdminUserDraft draft, CancellationToken cancellationToken = default) =>
        http.SendAsAccountProfileAsync(HttpMethod.Post, actor.Server, "api/v2/admin/users", actor.AccountId, actor.ProfileId, draft,
            AdminJsonContext.Default.AdminUserDraft, AdminJsonContext.Default.AdminUserCreated, cancellationToken);

    /// <summary>Changes the account at <paramref name="version"/>.</summary>
    public Task UpdateAsync(AdminActor actor, string id, string version, AdminUserChange change, CancellationToken cancellationToken = default) =>
        http.SendIfMatchAsAccountProfileAsync(HttpMethod.Put, actor.Server, UserPath(id), actor.AccountId, actor.ProfileId, version, change.ToJson(),
            AdminJsonContext.Default.JsonObject, cancellationToken);

    /// <summary>Deletes the account at <paramref name="version"/>, with its profiles and history.</summary>
    public Task DeleteAsync(AdminActor actor, string id, string version, CancellationToken cancellationToken = default) =>
        http.SendIfMatchAsAccountProfileAsync(HttpMethod.Delete, actor.Server, UserPath(id), actor.AccountId, actor.ProfileId, version, cancellationToken);

    /// <summary>Emails the account a reset link, or answers one to share (<see cref="AdminResetDelivery"/>); an earlier link stops working.</summary>
    public Task<AdminPasswordReset> IssuePasswordResetAsync(AdminActor actor, string id, string delivery, CancellationToken cancellationToken = default) =>
        http.SendAsAccountProfileAsync(HttpMethod.Post, actor.Server, UserPath(id) + "/password-reset", actor.AccountId, actor.ProfileId,
            new AdminPasswordResetRequest(delivery), AdminJsonContext.Default.AdminPasswordResetRequest, AdminJsonContext.Default.AdminPasswordReset,
            cancellationToken);

    /// <summary>Makes another enabled admin the server's Owner; only the Owner may, and stays an admin.</summary>
    public Task TransferOwnershipAsync(AdminActor actor, string id, CancellationToken cancellationToken = default) =>
        http.SendAsAccountProfileAsync(HttpMethod.Post, actor.Server, UserPath(id) + "/transfer-ownership", actor.AccountId, actor.ProfileId,
            cancellationToken);

    /// <summary>A page of the account's live sign-ins (not its playback), newest first, as the web's admin sessions panel.</summary>
    public Task<LoginSessionCollection> ListLoginSessionsAsync(AdminActor actor, string id, string? cursor = null,
        CancellationToken cancellationToken = default) =>
        http.GetAsAccountProfileAsync(actor.Server,
            UserPath(id) + "/login-sessions?limit=50" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}"),
            actor.AccountId, actor.ProfileId, DevicesJsonContext.Default.LoginSessionCollection, cancellationToken);

    /// <summary>Signs the account out of one sign-in.</summary>
    public Task SignOutSessionAsync(AdminActor actor, string id, string sessionId, CancellationToken cancellationToken = default) =>
        http.SendAsAccountProfileAsync(HttpMethod.Delete, actor.Server,
            $"{UserPath(id)}/login-sessions/{SiloHttp.PathSegment(sessionId, nameof(sessionId))}", actor.AccountId, actor.ProfileId, cancellationToken);

    /// <summary>Signs the account out everywhere; its password and devices stay.</summary>
    public Task<AdminSessionsRevoked> SignOutEverywhereAsync(AdminActor actor, string id, CancellationToken cancellationToken = default) =>
        http.SendAsAccountProfileAsync(HttpMethod.Delete, actor.Server, UserPath(id) + "/login-sessions", actor.AccountId, actor.ProfileId,
            AdminJsonContext.Default.AdminSessionsRevoked, cancellationToken);

    internal static string UserPath(string id) => $"api/v2/admin/users/{SiloHttp.PathSegment(id, nameof(id))}";

    private async Task<IReadOnlyList<T>> WalkAsync<TPage, T>(AdminActor actor, string path, JsonTypeInfo<TPage> pageType,
        Func<TPage, (IReadOnlyList<T> Items, AdminPageInfo? Page)> parts, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (int page = 0; page < MaxPages; page++)
        {
            string query = $"{path}?limit={PageSize}" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var (pageItems, info) = parts(await http.GetAsAccountProfileAsync(actor.Server, query, actor.AccountId, actor.ProfileId, pageType,
                cancellationToken));
            items.AddRange(pageItems);
            if (info is not { HasMore: true })
            {
                return items;
            }
            if (string.IsNullOrEmpty(info.NextCursor) || !cursors.Add(info.NextCursor))
            {
                throw new HttpRequestException(HttpRequestError.InvalidResponse, "The server's pages do not continue.");
            }
            cursor = info.NextCursor;
        }
        throw new HttpRequestException(HttpRequestError.InvalidResponse, "Too many pages.");
    }
}
