using System.Globalization;
using Granary.Core.Api.Admin;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// One account in Admin → Users, as the web's users table shows it (<c>useAdminUserRows.ts</c>): its role, group, status and
/// dates, and whether the viewer may open it to change it or delete it.
/// </summary>
public sealed record AdminAccountRow(
    string Id,
    string Username,
    string Email,
    string RoleText,
    bool IsAdmin,
    string GroupText,
    bool HasGroup,
    bool IsEnabled,
    string StatusText,
    string CreatedText,
    string CreatedTitle,
    string LastActiveText,
    string LastActiveTitle,
    bool CanEdit,
    bool CanDelete)
{
    public string EditName => AppText.Get("web.admin.users.list.editAria", ("username", Username));

    public string DeleteName => AppText.Get("web.admin.users.list.deleteAria", ("username", Username));

    public static AdminAccountRow From(AdminUser user, IReadOnlyDictionary<string, string> groupNames, string? viewerId, bool viewerIsOwner,
        DateTimeOffset now)
    {
        // Admins can't join a group; the others may have none.
        string group = user.IsAdmin ? AppText.Get("web.admin.users.list.adminGroupNone")
            : user.AccessGroupId is not { } id ? AppText.Get("web.admin.users.list.noGroup")
            : groupNames.TryGetValue(id, out var name) ? name
            : AppText.Get("web.admin.users.list.unknownGroup");
        bool canManage = AdminAccountRules.CanManage(user, viewerId, viewerIsOwner);
        return new AdminAccountRow(
            user.Id,
            user.Username,
            user.Email,
            AdminText.Role(user.Role, user.IsOwner),
            user.IsAdmin,
            group,
            !user.IsAdmin && user.AccessGroupId is not null,
            user.Enabled,
            AppText.Get(user.Enabled ? "web.admin.users.list.status.active" : "web.admin.users.list.status.disabled"),
            DateText.DateTime(user.CreatedAt.LocalDateTime, seconds: false, medium: true),
            DateText.DateTime(user.CreatedAt.LocalDateTime),
            user.LastActiveAt is { } at ? AdminText.Ago(at, now, AppText.Get("web.admin.dashboard.widgets.users.justNow"))
                : AppText.Get("web.admin.users.list.lastActiveNever"),
            user.LastActiveAt is { } seen ? DateText.DateTime(seen.LocalDateTime) : "",
            canManage,
            AdminAccountRules.CanDelete(user, viewerId, viewerIsOwner));
    }

    /// <summary>The sort of the web's <c>sortAdminUsers</c>: text by the language's rules, digits by value, case set aside.</summary>
    internal static int CompareText(string a, string b) =>
        string.Compare(a, b, AppText.Language.Culture,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth | CompareOptions.NumericOrdering);
}

/// <summary>The columns Users sorts by, in the web's column order.</summary>
public enum AdminUserSort
{
    Username,
    Email,
    Role,
    Status,
    Created,
    LastActive,
}

/// <summary>A choice of the group filter: every account, one group's, or those of no group.</summary>
public sealed record AdminGroupFilter(string Value, string Name)
{
    public const string All = "all";
    public const string None = "none";

    public override string ToString() => Name;
}
