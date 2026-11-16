using Granary.Core.Api.Admin;

namespace Granary.Core.ViewModels.Admin;

/// <summary>
/// Who may do what to an account, as the web's <c>lib/accountOwner.ts</c>. The server Owner set the server up, or had it
/// handed over: only the Owner grants the admin role, changes an admin's access, or changes, resets or deletes another
/// admin; nobody else changes the Owner's account, and the Owner cannot be deleted. The server enforces all of it; these
/// rules only keep Users from offering what it would refuse.
/// </summary>
public static class AdminAccountRules
{
    /// <summary>The viewer may edit, reset or manage the account.</summary>
    public static bool CanManage(AdminUser target, string? viewerId, bool viewerIsOwner)
    {
        if (target.Id == viewerId)
        {
            return true;
        }
        if (target.IsOwner)
        {
            return false;
        }
        return !target.IsAdmin || viewerIsOwner;
    }

    /// <summary>The viewer may change the account's libraries and permissions: only the Owner changes an admin's, its own too.</summary>
    public static bool CanChangeAccess(AdminUser target, string? viewerId, bool viewerIsOwner) =>
        CanManage(target, viewerId, viewerIsOwner) && (!target.IsAdmin || viewerIsOwner);

    /// <summary>The viewer may make the account the server's Owner.</summary>
    public static bool CanTransferOwnership(AdminUser target, string? viewerId, bool viewerIsOwner) =>
        viewerIsOwner && target.Id != viewerId && !target.IsOwner && target.IsAdmin && target.Enabled;

    /// <summary>The viewer may delete the account (or disable it): never the Owner, never the viewer's own.</summary>
    public static bool CanDelete(AdminUser target, string? viewerId, bool viewerIsOwner) =>
        !target.IsOwner && target.Id != viewerId && CanManage(target, viewerId, viewerIsOwner);

    /// <summary>Whether the viewer is the server's Owner, as the full list of accounts says.</summary>
    public static bool IsOwner(IEnumerable<AdminUser> users, string? viewerId) =>
        viewerId is not null && users.Any(user => user.Id == viewerId && user.IsOwner);
}
