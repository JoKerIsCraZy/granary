using System.Globalization;
using Granary.Core.Api.Catalog;
using Granary.Core.Texts;
using Granary.Core.ViewModels.Detail;
using Granary.Core.WatchParty;

namespace Granary.Core.ViewModels.WatchParty;

/// <summary>How the web's room names a title (<c>room/stage/StagedHero.tsx</c>), and says that a room is over.</summary>
public static class WatchPartyText
{
    // English whatever the language on screen, for text that is stored: the web's englishText.
    private static readonly Lazy<TextLanguage> English = new(() => TextLanguage.Load(AppLanguage.English));

    /// <summary>"1h 36m", "45m", as the web's <c>formatRuntimeMinutes</c>; empty without a length.</summary>
    public static string Runtime(int? minutes) => DetailText.Runtime(minutes) ?? "";

    /// <summary>"Evan Allmächtig (2007)", or an episode's <c>Show — S1 E2 "Title"</c>, in the language on screen.</summary>
    public static string Headline(CatalogItemBase item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Type == CatalogItemTypes.Episode && item.SeriesTitle is { Length: > 0 } series)
        {
            return item.Title.Length > 0
                ? AppText.Get("web.watchTogether.hero.episodeTitled",
                    ("series", series), ("season", Code(item.SeasonNumber)), ("episode", Code(item.EpisodeNumber)), ("title", item.Title))
                : AppText.Get("web.watchTogether.hero.episode", ("series", series), ("season", Code(item.SeasonNumber)), ("episode", Code(item.EpisodeNumber)));
        }
        return MovieYear(item.Title, item.Year);
    }

    /// <summary>
    /// <see cref="Headline(CatalogItemBase)"/> in English, as the room's name among the recent parties is stored: the web
    /// stores it with <c>englishText</c>, so it reads the same whatever language shows it.
    /// </summary>
    public static string StoredHeadline(CatalogItemBase item)
    {
        ArgumentNullException.ThrowIfNull(item);
        using (AppText.Use(English.Value))
        {
            return Headline(item);
        }
    }

    /// <summary>"Heat (1995)", or the title alone without a year.</summary>
    public static string MovieYear(string title, int? year) => year is { } known
        ? AppText.Get("web.watchTogether.hero.movieYear", ("title", title), ("year", known.ToString(CultureInfo.InvariantCulture)))
        : title;

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

    /// <summary>Parts joined with " · ", the empty ones left out, as the web joins a hero's facts.</summary>
    public static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>A season or episode number in a code ("S1 E2"): digits as written, as the web's <c>codeNumber</c>.</summary>
    public static string Code(int? number) => number?.ToString(CultureInfo.InvariantCulture) ?? "?";

    /// <summary>"A title's "S2 E3"" as the shelf and the activity name an episode: <c>S{season} E{episode}</c>.</summary>
    public static string EpisodeCode(int? season, int? episode) =>
        AppText.Get("web.watchTogether.common.episodeCode", ("season", Code(season)), ("episode", Code(episode)));

    /// <summary>
    /// Why a room is over, as the web's room page heads it (<c>WatchTogetherRoomPage.tsx</c>, <c>describeRoomError</c> and the
    /// replacement's title): "The room has ended.", "Room not found.", "Watch Party joined on another device", …
    /// </summary>
    public static string EndedTitle(WatchPartyEnd end)
    {
        ArgumentNullException.ThrowIfNull(end);
        return end.Replaced ? AppText.Get("web.watchTogether.room.replacedTitle") : end.Reason switch
        {
            "not_found" => AppText.Get("web.watchTogether.common.roomNotFound"),
            "ended" => AppText.Get("web.watchTogether.room.ended"),
            "forbidden" => AppText.Get("web.watchTogether.room.forbidden"),
            "host_left" or "room_closed" => AppText.Get("web.watchTogether.room.closed"),
            _ => AppText.Get("web.watchTogether.room.unavailable"),
        };
    }

    /// <summary>What comes next, under <see cref="EndedTitle"/>: the server's words for a takeover, else the web's way on.</summary>
    public static string EndedText(WatchPartyEnd end)
    {
        ArgumentNullException.ThrowIfNull(end);
        return end.Replaced
            ? end.Message is { Length: > 0 } message ? message : AppText.Get("web.player.watchTogether.connectionReplaced")
            : AppText.Get("web.watchTogether.room.closedBody");
    }

    /// <summary>
    /// A host's action that failed, and why: the web's words for what failed ("Could not start playback") and
    /// <see cref="LibraryErrors.Describe"/>'s for why.
    /// </summary>
    public static string Failed(string failure, string reason) =>
        AppText.Get("granary.watchParty.failedBecause", ("failure", failure), ("reason", reason));

    /// <summary>"Invite copied. Room code ABCD1234", as the web's <c>copyWatchTogetherInvite</c> says it.</summary>
    public static string InviteCopied(string? code) => code is { Length: > 0 }
        ? AppText.Get("web.watchTogether.actions.inviteCopiedWithCode", ("code", code))
        : AppText.Get("web.watchTogether.actions.inviteCopied");
}
