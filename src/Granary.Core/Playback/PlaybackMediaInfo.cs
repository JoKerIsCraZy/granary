using System.Globalization;
using Granary.Core.Api.Images;
using Granary.Core.Api.Playback;

namespace Granary.Core.Playback;

/// <summary>What kind of title plays, as far as the Discord status words it.</summary>
public enum PlaybackMediaKind
{
    /// <summary>No Silo title: a file or URL played directly, or nothing yet.</summary>
    Direct,

    /// <summary>A Silo title that is neither a movie nor an episode.</summary>
    Other,

    Movie,

    Episode,
}

/// <summary>
/// What Windows' media controls show for a title (the overlay of the media keys): two lines and a picture. As Silo's
/// apps fill their system media sessions, an episode shows its own title over its series and number (Android's
/// MediaSession: title, then "Series - S01E03"; the Apple app's Now Playing: the episode's title, "Episode 3" without
/// one), a movie its title over its year. The picture is the movie's poster or the episode's still, which the watch
/// detail sends for the system controls, else the backdrop (the Apple app's order). The other members are what the
/// Discord status words differently (<see cref="ViewModels.Player.PlayerRichPresence"/>); one made without them is no Silo
/// title (<see cref="PlaybackMediaKind.Direct"/>), as the player's for a file it plays directly.
/// </summary>
public sealed record PlaybackMediaInfo(string Title, string Subtitle, Uri? Artwork)
{
    public static PlaybackMediaInfo None { get; } = new("", "", null);

    public PlaybackMediaKind Kind { get; init; }

    /// <summary>An episode's series.</summary>
    public string? SeriesTitle { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    /// <summary>An episode's own title; null when it has none.</summary>
    public string? EpisodeTitle { get; init; }

    public int? Year { get; init; }

    /// <summary>The catalog item whose TMDB entry stands for the title: the movie itself, or an episode's series.</summary>
    public string? CatalogId { get; init; }

    public static PlaybackMediaInfo Of(WatchDetail detail, Uri serverBase)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var artwork = ArtworkUrl.Resolve(serverBase, detail.PosterUrl) ?? ArtworkUrl.Resolve(serverBase, detail.BackdropUrl);
        if (detail.Type != WatchItemTypes.Episode)
        {
            bool isMovie = detail.Type == WatchItemTypes.Movie;
            return OfTitle(detail.Title, detail.Year, isMovie, isMovie ? detail.ContentId : null, artwork);
        }
        return OfEpisode(detail.Title, detail.SeriesTitle, detail.SeasonNumber, detail.EpisodeNumber, detail.Year, detail.SeriesId, artwork);
    }

    /// <summary>A movie (<paramref name="isMovie"/>) or another Silo title that is no episode, its year under its title.</summary>
    /// <param name="catalogId">The movie's own catalog id; none for another title.</param>
    public static PlaybackMediaInfo OfTitle(string title, int? year, bool isMovie, string? catalogId, Uri? artwork) =>
        new(title, year?.ToString(CultureInfo.InvariantCulture) ?? "", artwork)
        {
            Kind = isMovie ? PlaybackMediaKind.Movie : PlaybackMediaKind.Other,
            Year = year,
            CatalogId = NullIfBlank(catalogId),
        };

    /// <summary>An episode: its own title ("Episode 3" without one) over its series and number.</summary>
    /// <param name="seriesId">The series' catalog id.</param>
    public static PlaybackMediaInfo OfEpisode(string? episodeTitle, string? seriesTitle, int? seasonNumber, int? episodeNumber, int? year,
        string? seriesId, Uri? artwork)
    {
        string title = string.IsNullOrWhiteSpace(episodeTitle) && episodeNumber is { } number
            ? string.Create(CultureInfo.InvariantCulture, $"Episode {number}")
            : episodeTitle ?? "";
        // The player's own code ("S1 · E5"), after the series as the apps write it.
        string code = seasonNumber is { } season && episodeNumber is { } episode
            ? string.Create(CultureInfo.InvariantCulture, $"S{season} · E{episode}")
            : "";
        string subtitle = string.Join(" — ", new[] { seriesTitle ?? "", code }.Where(part => part.Length > 0));
        return new(title, subtitle, artwork)
        {
            Kind = PlaybackMediaKind.Episode,
            SeriesTitle = NullIfBlank(seriesTitle),
            SeasonNumber = seasonNumber,
            EpisodeNumber = episodeNumber,
            EpisodeTitle = NullIfBlank(episodeTitle),
            Year = year,
            CatalogId = NullIfBlank(seriesId),
        };
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
