namespace Granary.App.Controls;

/// <summary>The sidebar's icons, for its entries and for the sidebar settings that list them.</summary>
public static class SidebarGlyphs
{
    /// <summary>An entry's icon by its tag, or a library's by its type (free text, docs/api-contract.md, 7.3).</summary>
    public static string Of(string kind) => kind switch
    {
        "ForYou" => "\uE734",
        "Requests" => "\uE724",
        "Calendar" => "\uE787",
        "Notifications" => "\uEA8F",
        "List:favorites" => "\uEB51",
        "List:watchlist" => "\uE8FD",
        "WatchParty" => "\uE716",
        "Collections" => "\uE838",
        "List:history" => "\uE81C",
        _ => Library(kind),
    };

    public static string Library(string type) => type.ToLowerInvariant() switch
    {
        "movies" or "movie" or "films" => "\uE8B2",
        "series" or "tv" or "shows" or "tvshows" => "\uE7F4",
        "music" => "\uE8D6",
        _ => "\uE8F1",
    };
}
