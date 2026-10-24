namespace Granary.Core.Abstractions;

/// <summary>Top-level destinations. The UI layer maps each route to a page.</summary>
public enum AppRoute
{
    Home,
    AddServer,
    Player,
}

public interface INavigationService
{
    AppRoute? Current { get; }

    void NavigateTo(AppRoute route, object? parameter = null);
}
