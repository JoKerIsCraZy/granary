using Granary.Core.Api.Auth;
using Granary.Tests.Browse;
using Granary.Tests.Fakes;

namespace Granary.Tests.Admin;

/// <summary>Admin → Users: the accounts, groups and capabilities a server answers, in its wire format.</summary>
internal static class AdminUserPayloads
{
    public static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 30, 0, TimeSpan.Zero);

    /// <summary>The Owner (1, the viewer), an admin (2), a grouped user (3), an ungrouped disabled user (4).</summary>
    public const string Users = """
        {
          "items": [
            { "id": "1", "username": "elena", "email": "elena@example.com", "role": "admin", "permissions": [], "enabled": true,
              "library_ids": null, "access_group_id": null, "password_login": true, "password_change_required": false, "is_owner": true,
              "break_glass": false, "created_at": "2026-01-02T10:00:00.000Z", "updated_at": "2026-01-02T10:00:00.000Z",
              "last_active_at": "2026-10-27T12:25:00.000Z" },
            { "id": "2", "username": "kim", "email": "kim@example.com", "role": "admin", "permissions": [], "enabled": true,
              "library_ids": null, "access_group_id": null, "password_login": true, "password_change_required": false, "is_owner": false,
              "break_glass": false, "created_at": "2026-02-02T10:00:00.000Z", "updated_at": "2026-02-02T10:00:00.000Z", "last_active_at": null }
          ],
          "page": { "has_more": true, "next_cursor": "c1" }
        }
        """;

    public const string UsersPage2 = """
        {
          "items": [
            { "id": "3", "username": "ana", "email": "ana@example.com", "role": "user", "permissions": ["marker_edit"], "enabled": true,
              "library_ids": ["5"], "access_group_id": "10", "password_login": true, "password_change_required": true, "is_owner": false,
              "break_glass": false, "created_at": "2026-03-02T10:00:00.000Z", "updated_at": "2026-03-02T10:00:00.000Z",
              "last_active_at": "2026-10-25T12:30:00.000Z" },
            { "id": "4", "username": "Bo", "email": "bo@example.com", "role": "user", "permissions": [], "enabled": false,
              "library_ids": null, "access_group_id": null, "password_login": false, "password_change_required": false, "is_owner": false,
              "break_glass": false, "created_at": "2026-04-02T10:00:00.000Z", "updated_at": "2026-04-02T10:00:00.000Z", "last_active_at": null }
          ],
          "page": { "has_more": false }
        }
        """;

    public const string Groups = """
        { "items": [ { "id": "10", "name": "Family", "is_default": true }, { "id": "11", "name": "Friends", "is_default": false } ], "page": { "has_more": false } }
        """;

    public const string Capabilities = """
        { "state": "available", "available": true, "default_profile": true, "access_groups": true, "password_reset_link": true,
          "password_reset_email": false, "ownership_transfer": true, "login_sessions": true }
        """;

    /// <summary>A server answering Users: two pages of accounts, the groups and the capabilities.</summary>
    public static BrowseFakeSilo Silo()
    {
        var silo = new BrowseFakeSilo();
        silo.Map("api/v2/admin/users", request => BrowseFakeSilo.Json(request.RequestUri!.Query.Contains("cursor=c1") ? UsersPage2 : Users));
        silo.Map("api/v2/admin/access-groups", Groups);
        silo.Map("api/v2/admin/users/capabilities", Capabilities);
        return silo;
    }

    /// <summary>A session signed in as the Owner.</summary>
    public static FakeSessionManager OwnerSession()
    {
        var session = BrowseFakeSilo.ReadySession();
        session.Account = new Account("1", "elena", "elena@example.com", "admin", [], true, false, null);
        return session;
    }

    /// <summary>One account as <c>GET /api/v2/admin/users/{id}</c> answers it, Ana by default.</summary>
    public static string User(string id = "3", string username = "ana", string role = "user", bool enabled = true, string group = "\"10\"") => $$"""
        { "id": "{{id}}", "username": "{{username}}", "email": "{{username}}@example.com", "role": "{{role}}", "permissions": ["marker_edit"],
          "enabled": {{(enabled ? "true" : "false")}}, "library_ids": null, "access_group_id": {{group}}, "password_login": true,
          "password_change_required": false, "is_owner": false, "break_glass": false, "created_at": "2026-03-02T10:00:00.000Z",
          "updated_at": "2026-03-02T10:00:00.000Z", "last_active_at": null }
        """;

    /// <summary>The account with its version.</summary>
    public static HttpResponseMessage Versioned(string json, string version = "\"v1\"")
    {
        var response = BrowseFakeSilo.Json(json);
        response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(version);
        return response;
    }
}
