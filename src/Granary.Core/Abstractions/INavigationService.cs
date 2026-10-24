namespace Granary.Core.Abstractions;

/// <summary>Top-level destinations. The UI layer maps each route to a page.</summary>
public enum AppRoute
{
    Home,

    /// <summary>Saved servers: switch, sign out, remove.</summary>
    Servers,

    AddServer,

    /// <summary>Sign-in for a saved server; the parameter is the <c>ServerEntry.Id</c>.</summary>
    SignIn,

    ProfilePicker,

    /// <summary>Change a temporary password before the account can be used.</summary>
    ChangePassword,

    Player,
}

public interface INavigationService
{
    AppRoute? Current { get; }

    void NavigateTo(AppRoute route, object? parameter = null);
}
