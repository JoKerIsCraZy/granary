namespace Granary.Core.ViewModels.Admin;

/// <summary>The pages of the admin area, as <c>AppRoute.Admin</c>'s parameter names them.</summary>
public static class AdminPages
{
    public const string Dashboard = "dashboard";

    /// <summary>The playback sessions running now.</summary>
    public const string Activity = "activity";

    /// <summary>The accounts, their invitations and invite codes.</summary>
    public const string Users = "users";

    /// <summary>Users on its Invitations tab, as the web's <c>/admin/users?tab=invitations</c>.</summary>
    public const string Invitations = "invitations";
}
