using System.Text.Json.Serialization.Metadata;

namespace Granary.Core.Api.Admin;

/// <summary>
/// The server's login accounts, as the web's Admin → Users reads and changes them (<c>api/v2/adminUsers.ts</c>,
/// docs/api-contract.md, 7.17). Every call needs an administrator account. A change or delete carries the version the
/// account was read at (<c>If-Match</c>): 412 means another administrator changed it since.
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
    public Task<IReadOnlyList<AdminUser>> ListAllAsync(Uri server, CancellationToken cancellationToken = default) =>
        WalkAsync(server, "api/v2/admin/users", AdminJsonContext.Default.AdminUserCollection, page => (page.Items, page.Page), cancellationToken);

    /// <summary>Every access group, for their names and the group new accounts join.</summary>
    public Task<IReadOnlyList<AdminAccessGroup>> ListAccessGroupsAsync(Uri server, CancellationToken cancellationToken = default) =>
        WalkAsync(server, "api/v2/admin/access-groups", AdminJsonContext.Default.AdminAccessGroupCollection, page => (page.Items, page.Page),
            cancellationToken);

    public Task<AdminUserCapabilities> GetCapabilitiesAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, "api/v2/admin/users/capabilities", SiloAuth.Profile, AdminJsonContext.Default.AdminUserCapabilities, cancellationToken);

    /// <summary>One account and the version a change or delete must name.</summary>
    public Task<Versioned<AdminUser>> GetAsync(Uri server, string id, CancellationToken cancellationToken = default) =>
        http.GetVersionedAsync(server, UserPath(id), SiloAuth.Profile, AdminJsonContext.Default.AdminUser, cancellationToken);

    /// <summary>Creates the account; 409 when its username or email is taken.</summary>
    public Task<AdminUserCreated> CreateAsync(Uri server, AdminUserDraft draft, CancellationToken cancellationToken = default) =>
        http.PostAsync(server, "api/v2/admin/users", SiloAuth.Profile, draft, AdminJsonContext.Default.AdminUserDraft,
            AdminJsonContext.Default.AdminUserCreated, cancellationToken);

    /// <summary>Changes the account at <paramref name="version"/>.</summary>
    public Task UpdateAsync(Uri server, string id, string version, AdminUserChange change, CancellationToken cancellationToken = default) =>
        http.SendIfMatchAsync(HttpMethod.Put, server, UserPath(id), SiloAuth.Profile, version, change.ToJson(), AdminJsonContext.Default.JsonObject,
            cancellationToken);

    /// <summary>Deletes the account at <paramref name="version"/>, with its profiles and history.</summary>
    public Task DeleteAsync(Uri server, string id, string version, CancellationToken cancellationToken = default) =>
        http.SendIfMatchAsync(HttpMethod.Delete, server, UserPath(id), SiloAuth.Profile, version, cancellationToken);

    /// <summary>Emails the account a reset link, or answers one to share (<see cref="AdminResetDelivery"/>); an earlier link stops working.</summary>
    public Task<AdminPasswordReset> IssuePasswordResetAsync(Uri server, string id, string delivery, CancellationToken cancellationToken = default) =>
        http.PostAsync(server, UserPath(id) + "/password-reset", SiloAuth.Profile, new AdminPasswordResetRequest(delivery),
            AdminJsonContext.Default.AdminPasswordResetRequest, AdminJsonContext.Default.AdminPasswordReset, cancellationToken);

    /// <summary>Makes another enabled admin the server's Owner; only the Owner may, and stays an admin.</summary>
    public Task TransferOwnershipAsync(Uri server, string id, CancellationToken cancellationToken = default) =>
        http.SendAsync(HttpMethod.Post, server, UserPath(id) + "/transfer-ownership", SiloAuth.Profile, cancellationToken);

    internal static string UserPath(string id) => $"api/v2/admin/users/{SiloHttp.PathSegment(id, nameof(id))}";

    private async Task<IReadOnlyList<T>> WalkAsync<TPage, T>(Uri server, string path, JsonTypeInfo<TPage> pageType,
        Func<TPage, (IReadOnlyList<T> Items, AdminPageInfo? Page)> parts, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (int page = 0; page < MaxPages; page++)
        {
            string query = $"{path}?limit={PageSize}" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var (pageItems, info) = parts(await http.GetAsync(server, query, SiloAuth.Profile, pageType, cancellationToken));
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
