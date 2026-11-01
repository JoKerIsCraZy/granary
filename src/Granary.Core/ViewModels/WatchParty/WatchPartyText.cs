using System.Globalization;
using Granary.Core.Api.Catalog;
using Granary.Core.WatchParty;

namespace Granary.Core.ViewModels.WatchParty;

/// <summary>How the web's room names a title (<c>room/stage/StagedHero.tsx</c>), and says that a room is over.</summary>
public static class WatchPartyText
{
    /// <summary>"1h 36m", "45m"; empty without a length.</summary>
    public static string Runtime(int? minutes) => minutes is int m && m > 0
        ? m >= 60 ? $"{m / 60}h {m % 60}m" : $"{m}m"
        : "";

    /// <summary>"Evan Allmächtig (2007)", or an episode's "Show — S1 E2 \"Title\"".</summary>
    public static string Headline(CatalogItemBase item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Type == CatalogItemTypes.Episode && item.SeriesTitle is { Length: > 0 } series)
        {
            string code = $"S{Number(item.SeasonNumber)} E{Number(item.EpisodeNumber)}";
            return item.Title.Length > 0 ? $"{series} — {code} “{item.Title}”" : $"{series} — {code}";
        }
        return item.Year is { } year ? $"{item.Title} ({year.ToString(CultureInfo.InvariantCulture)})" : item.Title;
    }

    /// <summary>"1h 36m · PG · Fantasy, Komödie": a movie's first two genres, none for a series or an episode.</summary>
    public static string Meta(CatalogItemBase item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var parts = new List<string>();
        if (Runtime(item.Runtime) is { Length: > 0 } runtime)
        {
            parts.Add(runtime);
        }
        if (item.ContentRating is { Length: > 0 } rating)
        {
            parts.Add(rating);
        }
        if (item.Type == CatalogItemTypes.Movie && item.Genres.Count > 0)
        {
            parts.Add(string.Join(", ", item.Genres.Take(2)));
        }
        return string.Join(" · ", parts);
    }

    /// <summary>Parts joined with " · ", the empty ones left out.</summary>
    public static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>
    /// Why a room is over, as the web's room page heads it (<c>WatchTogetherRoomPage.tsx</c>, <c>describeRoomError</c> and the
    /// replacement's title): "The room has ended.", "Room not found.", "Watch Party joined on another device", …
    /// </summary>
    public static string EndedTitle(WatchPartyEnd end)
    {
        ArgumentNullException.ThrowIfNull(end);
        return end.Replaced ? "Watch Party joined on another device" : end.Reason switch
        {
            "not_found" => "Room not found.",
            "ended" => "That watch party is no longer active.",
            "forbidden" => "You don't have access to this watch party.",
            "host_left" or "room_closed" => "The room has ended.",
            _ => "Room is unavailable.",
        };
    }

    /// <summary>What comes next, under <see cref="EndedTitle"/>: the server's words for a takeover, else the web's way on.</summary>
    public static string EndedText(WatchPartyEnd end)
    {
        ArgumentNullException.ThrowIfNull(end);
        return end.Replaced
            ? end.Message is { Length: > 0 } message ? message : WatchPartyFrames.ReplacedText
            : "Start a new watch party or join another room to keep watching together.";
    }

    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "?";
}
