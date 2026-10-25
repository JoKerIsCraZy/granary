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

    /// <summary>Search; the parameter is an optional query text.</summary>
    Search,

    /// <summary>One library; the parameter is the library id.</summary>
    Library,

    /// <summary>A movie, series, season or episode; the parameter is its catalog content id.</summary>
    Item,

    /// <summary>"Explore all" of a Home or library row; the parameter is a <see cref="SectionRouteParameter"/>.</summary>
    Section,

    /// <summary>The profile's settings; the parameter is an optional page ("playback", "subtitles").</summary>
    Settings,

    /// <summary>Favorites, Watchlist or History; the parameter is the list's source (<c>favorites</c>, <c>watchlist</c>, <c>history</c>).</summary>
    List,

    /// <summary>The profile's own collections, the ones other profiles share with it, and the server's.</summary>
    Collections,

    /// <summary>One collection's titles; the parameter is a <see cref="CollectionRouteParameter"/>.</summary>
    Collection,

    /// <summary>The profile's recommendation rows.</summary>
    ForYou,

    /// <summary>A For You row in full; the parameter is a <see cref="RecommendationSectionRouteParameter"/>.</summary>
    RecommendationSection,
}

/// <summary>Which row an "Explore all" grid lists: a Home row, or a library row when <see cref="LibraryId"/> is set.</summary>
/// <param name="SectionType">The row's <c>section_type</c>; Continue Watching and Next Up list landscape cards.</param>
public sealed record SectionRouteParameter(string SectionId, string Title, string? LibraryId = null, string? SectionType = null);

/// <summary>A collection whose titles a list shows in the collection's own order.</summary>
/// <param name="Source"><c>user_collection</c> for a personal collection, <c>library_collection</c> for a server one.</param>
/// <param name="LibraryId">The library a server collection opens in; null opens it across every library that has it.</param>
public sealed record CollectionRouteParameter(string Source, string CollectionId, string Title, string? LibraryId = null);

/// <summary>Which For You row a grid lists in full.</summary>
/// <param name="Kind">One of <c>RecommendationSectionKinds</c>.</param>
/// <param name="Key">A cluster's index or a genre's name, when the kind needs one.</param>
public sealed record RecommendationSectionRouteParameter(string Kind, string? Key, string Title);

public interface INavigationService
{
    AppRoute? Current { get; }

    bool CanGoBack { get; }

    void NavigateTo(AppRoute route, object? parameter = null);

    /// <summary>Navigates and forgets the history, for example after sign-in, so Back cannot return to it.</summary>
    void ResetTo(AppRoute route, object? parameter = null);

    void GoBack();
}
