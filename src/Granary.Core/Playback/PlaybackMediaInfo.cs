using System.Globalization;
using Granary.Core.Api.Images;
using Granary.Core.Api.Playback;

namespace Granary.Core.Playback;

/// <summary>
/// What Windows' media controls show for a title (the overlay of the media keys): two lines and a picture. As Silo's
/// apps fill their system media sessions, an episode shows its own title over its series and number (Android's
/// MediaSession: title, then "Series - S01E03"; the Apple app's Now Playing: the episode's title, "Episode 3" without
/// one), a movie its title over its year. The picture is the movie's poster or the episode's still, which the watch
/// detail sends for the system controls, else the backdrop (the Apple app's order).
/// </summary>
public sealed record PlaybackMediaInfo(string Title, string Subtitle, Uri? Artwork)
{
    public static PlaybackMediaInfo None { get; } = new("", "", null);

    public static PlaybackMediaInfo Of(WatchDetail detail, Uri serverBase)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var artwork = ArtworkUrl.Resolve(serverBase, detail.PosterUrl) ?? ArtworkUrl.Resolve(serverBase, detail.BackdropUrl);
        if (detail.Type != WatchItemTypes.Episode)
        {
            return new(detail.Title, detail.Year?.ToString(CultureInfo.InvariantCulture) ?? "", artwork);
        }
        string title = string.IsNullOrWhiteSpace(detail.Title) && detail.EpisodeNumber is { } number
            ? string.Create(CultureInfo.InvariantCulture, $"Episode {number}")
            : detail.Title;
        // The player's own code ("S1 · E5"), after the series as the apps write it.
        string code = detail.SeasonNumber is { } season && detail.EpisodeNumber is { } episode
            ? string.Create(CultureInfo.InvariantCulture, $"S{season} · E{episode}")
            : "";
        string subtitle = string.Join(" — ", new[] { detail.SeriesTitle ?? "", code }.Where(part => part.Length > 0));
        return new(title, subtitle, artwork);
    }
}
