using System.Globalization;
using Granary.Core.Api.Images;
using Granary.Core.Api.Playback;

namespace Granary.Core.Playback;

/// <summary>What kind of title plays, as far as the Discord status words it.</summary>
public enum PlaybackMediaKind
{
    /// <summary>A local file, or a title that is neither a movie nor an episode.</summary>
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
/// Discord status words differently (<see cref="ViewModels.Player.PlayerRichPresence"/>).
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
            return new(detail.Title, detail.Year?.ToString(CultureInfo.InvariantCulture) ?? "", artwork)
            {
                Kind = isMovie ? PlaybackMediaKind.Movie : PlaybackMediaKind.Other,
                Year = detail.Year,
                CatalogId = isMovie ? NullIfBlank(detail.ContentId) : null,
            };
        }
        string title = string.IsNullOrWhiteSpace(detail.Title) && detail.EpisodeNumber is { } number
            ? string.Create(CultureInfo.InvariantCulture, $"Episode {number}")
            : detail.Title;
        // The player's own code ("S1 · E5"), after the series as the apps write it.
        string code = detail.SeasonNumber is { } season && detail.EpisodeNumber is { } episode
            ? string.Create(CultureInfo.InvariantCulture, $"S{season} · E{episode}")
            : "";
        string subtitle = string.Join(" — ", new[] { detail.SeriesTitle ?? "", code }.Where(part => part.Length > 0));
        return new(title, subtitle, artwork)
        {
            Kind = PlaybackMediaKind.Episode,
            SeriesTitle = NullIfBlank(detail.SeriesTitle),
            SeasonNumber = detail.SeasonNumber,
            EpisodeNumber = detail.EpisodeNumber,
            EpisodeTitle = NullIfBlank(detail.Title),
            Year = detail.Year,
            CatalogId = NullIfBlank(detail.SeriesId),
        };
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
